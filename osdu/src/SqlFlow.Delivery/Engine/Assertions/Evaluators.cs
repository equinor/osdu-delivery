using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>What an assertion reads of the records a test matches: nothing (the search answers it), their ids, or their fields.</summary>
public enum RecordNeed
{
    None = 0,
    Ids = 1,
    Fields = 2,
}

/// <summary>What a test found before its assertions complete: how many records matched, how many it read, and why it read none.</summary>
/// <param name="Matched">The index's exact count, or the records the ids name that storage holds; null when the test did not get as far.</param>
/// <param name="Read">The records the test read and every evaluator observed.</param>
/// <param name="Sampled">True when more records matched than the test reads and it read the first ones.</param>
/// <param name="NotRead">Why the records were not read (more matched than the test reads), or null when they were, or were not needed.</param>
/// <param name="Missing">The ids a test named by id that storage did not return.</param>
public sealed record TestSubject(long? Matched, long Read, bool Sampled, string? NotRead, IReadOnlyList<string> Missing);

/// <summary>Everything the evaluators of one test share while it runs.</summary>
public sealed class TestScope
{
    public required AssertionTest Test { get; init; }

    /// <summary>The template the test's fields were checked against, or null when it reads none.</summary>
    public OsduTemplate? Template { get; init; }

    /// <summary>When the run judges the test: what a <c>validUntil</c> is held against.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The templates of the records the test reads through references, by entity type (<see cref="TemplateFit.Referenced"/>).</summary>
    public IReadOnlyDictionary<string, OsduTemplate> Referenced { get; init; } = new Dictionary<string, OsduTemplate>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The failing examples an assertion names at most.</summary>
    public required int Examples { get; init; }

    /// <summary>The test's search: its kind, query (tokens substituted), spatial filter and sort.</summary>
    public required OsduSearchQuery Query { get; init; }

    public required OsduSearch Search { get; init; }

    public required StorageRecords Storage { get; init; }

    public required LegalTagValidator Legal { get; init; }

    public required WellboreBulk Bulk { get; init; }

    /// <summary>The module's ledger, which the delivered assertion reads; null on a host without the module database.</summary>
    public ILedger? Ledger { get; init; }

    /// <summary>Opens the rows a delivery flow stages, which a staged delivered assertion accounts for; null on a host that opens none.</summary>
    public IStagedDeliveries? Deliveries { get; init; }

    /// <summary>
    /// Why each assertion that compares with a query was not judged, by its index in the test: its query failed, or answered
    /// what it cannot compare with (osdu/docs/reference/flow/staged-proof-values.md).
    /// </summary>
    public IReadOnlyDictionary<int, string> Unanswered { get; init; } = new Dictionary<int, string>();

    /// <summary>What each query the test compares with answered, which its result notes first.</summary>
    public IReadOnlyList<string> Answers { get; init; } = [];

    /// <summary>The partition the run tests, as its data-partition-id.</summary>
    public required string Partition { get; init; }

    /// <summary>How many requests the test has in flight at once.</summary>
    public required int Concurrency { get; init; }

    public required ILogger Logger { get; init; }
}

/// <summary>
/// Evaluates one assertion of a test: it states what it reads, observes every record the test reads in order (a single
/// reader calls it, never two at once), and completes with the assertion's outcome, asking the platform whatever else it
/// needs (a grouping, the index's status, whether references resolve, whether legal tags are valid). Whatever it keeps
/// while observing is bounded by the records the test reads.
/// </summary>
public abstract class Evaluator
{
    protected Evaluator(int index, TestAssertion assertion, int examples)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        Index = index;
        Assertion = assertion;
        Examples = examples;
    }

    public int Index { get; }

    public TestAssertion Assertion { get; }

    /// <summary>The failing examples the outcome names at most.</summary>
    protected int Examples { get; }

    public virtual RecordNeed Need => RecordNeed.None;

    /// <summary>The record paths the evaluator reads, which a test reading the index asks the search to return.</summary>
    public virtual IEnumerable<string> Paths => [];

    public virtual void Observe(string id, JsonNode record)
    {
    }

    public abstract Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct);

    /// <summary>The outcome the assertion comes to.</summary>
    protected AssertionOutcome Result(
        bool passed, string? actual, string? message, long? checkedCount = null, long? failing = null, double? value = null,
        IReadOnlyList<AssertionExample>? examples = null) => new()
        {
            Index = Index,
            Label = Assertion.Label,
            Description = Assertion.Description,
            Type = Assertion.Type,
            Severity = AssertionText.Of(Assertion.Severity),
            Outcome = passed ? TestOutcomes.Passed : TestOutcomes.Failed,
            Expected = Assertion.Expected,
            Actual = actual,
            Message = passed ? null : message,
            Checked = checkedCount,
            Failing = failing,
            Value = value,
            Examples = examples ?? [],
        };

    /// <summary>The outcome of an assertion over records a test did not read, saying why.</summary>
    protected AssertionOutcome NotRead(TestSubject subject) => AssertionOutcome.Errored(Index, Assertion, subject.NotRead!);

    /// <summary>
    /// The outcome of an assertion that does not hold, having missed what it expects by no more than its warning band
    /// (osdu/docs/reference/flow/assertion.md, A warning band): it weighs a warning, or its own severity when that is milder,
    /// so its test warns rather than fails, and its message says by how much it missed.
    /// </summary>
    protected AssertionOutcome Warned(AssertionOutcome outcome, string why)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome with
        {
            Severity = AssertionText.Of(Severities.Milder(AssertionSeverity.Warning, Assertion.Severity)),
            Message = (outcome.Message is { Length: > 0 } message ? message.TrimEnd('.') + "; " : string.Empty) + why + ".",
        };
    }

    /// <summary>What a miss within a warning band says: by how much, when one value missed, and that it warns.</summary>
    internal static string WithinBand(WarningBand band, string? miss)
        => $"{(miss is null ? string.Empty : miss + ", ")}within the warning band of {band}, so it warns";

    /// <summary>The evaluators of a test's assertions, in document order.</summary>
    public static IReadOnlyList<Evaluator> For(TestScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var examples = scope.Examples;
        return scope.Test.Assertions.Select((assertion, index) => assertion switch
        {
            CountAssertion count => (Evaluator)new CountEvaluator(index, count, examples),
            ValueAssertion { Target.IsColumn: true } column => new ColumnValueEvaluator(index, column, examples),
            ValueAssertion { Condition.Operator: ValueOperator.Resolves } references => new ResolvesEvaluator(index, references, examples),
            ValueAssertion field => new FieldEvaluator(index, field, examples),
            AggregateAssertion { Target.IsColumn: true } aggregate => new ColumnAggregateEvaluator(index, aggregate, examples),
            AggregateAssertion aggregate => new AggregateEvaluator(index, aggregate, examples),
            UniqueAssertion unique => new UniqueEvaluator(index, unique, examples),
            GroupAssertion group => new GroupEvaluator(index, group, examples),
            RecordSetAssertion set => new RecordSetEvaluator(index, set, examples),
            KnownValuesAssertion known => new KnownValuesEvaluator(index, known, examples),
            ConformsAssertion conforms => new ConformsEvaluator(index, conforms, scope),
            MappingRulesAssertion rules => new MappingRulesEvaluator(index, rules, examples),
            IndexedAssertion indexed => new IndexedEvaluator(index, indexed, examples),
            LegalAssertion legal => new LegalEvaluator(index, legal, examples),
            DeliveredAssertion { Staged: true } staged => new StagedAccountingEvaluator(index, staged, examples, scope.Test, string.Create(CultureInfo.InvariantCulture, $"{scope.Test.Name}: assert[{index}]")),
            DeliveredAssertion delivered => new DeliveredEvaluator(index, delivered, examples, scope.Test),
            RowCountAssertion rows => new RowCountEvaluator(index, rows, examples),
            ColumnsAssertion columns => new ColumnsEvaluator(index, columns, examples),
            MonotonicAssertion monotonic => new MonotonicEvaluator(index, monotonic, examples),
            _ => throw new InvalidOperationException($"No evaluator for a '{assertion.Type}' assertion."),
        }).Where(e => AssertionActivity.Disabled(e.Assertion) is null && AssertionActivity.Stale(e.Assertion, scope.Test, scope.Now) is null && !scope.Unanswered.ContainsKey(e.Index)).ToList();
    }

    /// <summary>Whether a record (or row) meets every one of an assertion's where conditions: one of its values meets each.</summary>
    internal static bool Selected(IReadOnlyList<ValueFilter> filters, Func<ValueTarget, IReadOnlyList<JsonNode>> values)
    {
        foreach (var filter in filters)
        {
            if (!ValueComparer.Selects(filter.Condition, values(filter.Target)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Counts how an assertion held over records (or rows) and decides it by its quantifier: all of them, at least one, none,
/// or a share. The examples are the ones that failed it, or for <c>none</c>, the ones that met it.
/// </summary>
public sealed class Tally
{
    private readonly Quantifier _for;
    private readonly int _cap;
    private readonly List<AssertionExample> _failing = [];
    private readonly List<AssertionExample> _meeting = [];

    public Tally(Quantifier quantifier, int cap)
    {
        ArgumentNullException.ThrowIfNull(quantifier);
        _for = quantifier;
        _cap = cap;
    }

    /// <summary>The records the condition was decided for.</summary>
    public long Considered { get; private set; }

    public long Meeting { get; private set; }

    /// <summary>Records passed over: optional ones with no value.</summary>
    public long Skipped { get; set; }

    /// <summary>Records the where conditions left out.</summary>
    public long Filtered { get; set; }

    public void Add(bool holds, string? id, string? value, string reason)
    {
        Considered++;
        if (holds)
        {
            Meeting++;
            if (_meeting.Count < _cap)
            {
                _meeting.Add(new AssertionExample(id, value, "meets it"));
            }
        }
        else if (_failing.Count < _cap)
        {
            _failing.Add(new AssertionExample(id, value, reason));
        }
    }

    public bool Passed => _for.Mode switch
    {
        QuantifierMode.All => Considered > 0 && Meeting == Considered,
        QuantifierMode.Any => Meeting > 0,
        QuantifierMode.None => Meeting == 0,
        _ => Considered > 0 && Meeting * 100.0 / Considered >= _for.Share,
    };

    /// <summary>How many records fail the assertion: those not meeting it, or for <c>none</c>, those meeting it.</summary>
    public long Failing => _for.Mode == QuantifierMode.None ? Meeting : Considered - Meeting;

    public IReadOnlyList<AssertionExample> Examples => _for.Mode == QuantifierMode.None ? _meeting : _failing;

    /// <summary>The share meeting the condition, in percent, for a trend across runs.</summary>
    public double? Share => Considered == 0 ? null : Math.Round(Meeting * 100.0 / Considered, 3);

    public string Actual(string unit)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Meeting} of {Considered} {unit}(s) meet it");
        if (Considered > 0 && _for.Mode == QuantifierMode.Share)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" ({Share:0.###}%)");
        }

        if (Skipped > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"; {Skipped} with no value passed over");
        }

        if (Filtered > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $"; {Filtered} left out by where");
        }

        return text;
    }

    public string Message(string unit) => Considered == 0 && _for.Mode is not QuantifierMode.None
        ? $"No {unit} was there to check: none matched, none met the where conditions, or none had a value."
        : _for.Mode switch
        {
            QuantifierMode.All => string.Create(CultureInfo.InvariantCulture, $"{Failing} of {Considered} {unit}(s) do not meet it."),
            QuantifierMode.Any => $"No {unit} meets it.",
            QuantifierMode.None => string.Create(CultureInfo.InvariantCulture, $"{Meeting} {unit}(s) meet it, and none may."),
            _ => string.Create(CultureInfo.InvariantCulture, $"{Share:0.###}% of the {unit}s meet it, below the {_for.Share:0.###}% asked for."),
        };
}

/// <summary>How many records the test matches.</summary>
internal sealed class CountEvaluator(int index, CountAssertion assertion, int examples) : Evaluator(index, assertion, examples)
{
    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.Matched is not { } matched)
        {
            return Task.FromResult(AssertionOutcome.Errored(Index, Assertion, "The test did not get as far as counting its records."));
        }

        var count = Measured.Of(matched);
        var passed = ValueComparer.Evaluate(assertion.Comparison, count, null);
        var missing = subject.Missing.Take(Examples).Select(id => new AssertionExample(id, null, "storage does not return it: it was never written, was deleted, or this identity may not read it")).ToList();
        var outcome = Result(
            passed,
            string.Create(CultureInfo.InvariantCulture, $"{matched} record(s)"),
            string.Create(CultureInfo.InvariantCulture, $"The test matches {matched} record(s); it expects {assertion.Comparison}.")
                + (subject.Missing.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" {subject.Missing.Count} of the ids it names are not in storage.") : string.Empty),
            matched,
            passed ? 0 : 1,
            matched,
            missing);
        return Task.FromResult(!passed && assertion.WarnWithin is { } band && ValueComparer.Within(assertion.Comparison, count, null, band)
            ? Warned(outcome, WithinBand(band, ValueComparer.Miss(assertion.Comparison, count, null)))
            : outcome);
    }
}

/// <summary>A condition on the values of a field, held for the share of records the quantifier asks.</summary>
internal sealed class FieldEvaluator : Evaluator
{
    private readonly ValueAssertion _assertion;
    private readonly Tally _tally;

    public FieldEvaluator(int index, ValueAssertion assertion, int examples)
        : base(index, assertion, examples)
    {
        _assertion = assertion;
        _tally = new Tally(assertion.For, examples);
    }

    public override RecordNeed Need => RecordNeed.Fields;

    public override IEnumerable<string> Paths => _assertion.Where.Select(w => w.Target.Path).Prepend(_assertion.Target.Path);

    public override void Observe(string id, JsonNode record)
    {
        if (!Selected(_assertion.Where, target => RecordValues.Select(record, target.Path)))
        {
            _tally.Filtered++;
            return;
        }

        var values = RecordValues.Select(record, _assertion.Target.Path);
        var condition = _assertion.Condition;
        if (values.Count == 0)
        {
            switch (condition.Operator)
            {
                case ValueOperator.Exists:
                    _tally.Add(!condition.Flag, id, "(no value)", "has no value");
                    return;
                case ValueOperator.Empty:
                    _tally.Add(condition.Flag, id, "(no value)", "has no value, and is expected to hold one");
                    return;
                default:
                    if (_assertion.Optional)
                    {
                        _tally.Skipped++;
                    }
                    else
                    {
                        _tally.Add(false, id, "(no value)", "has no value");
                    }

                    return;
            }
        }

        if (condition.Operator == ValueOperator.Exists)
        {
            _tally.Add(condition.Flag, id, RecordValues.Describe(values), "has a value, and is expected to have none");
            return;
        }

        var reason = string.Empty;
        var holding = 0;
        foreach (var value in values)
        {
            if (ValueComparer.Holds(condition, value, out var why))
            {
                holding++;
            }
            else if (reason.Length == 0)
            {
                reason = why;
            }
        }

        var holds = _assertion.AnyValue ? holding > 0 : holding == values.Count;
        _tally.Add(holds, id, RecordValues.Describe(values), values.Count > 1 && !_assertion.AnyValue
            ? string.Create(CultureInfo.InvariantCulture, $"{values.Count - holding} of its {values.Count} values do not meet it; one {reason}")
            : reason);
    }

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
        => Task.FromResult(subject.NotRead is not null
            ? NotRead(subject)
            : Result(_tally.Passed, _tally.Actual("record"), _tally.Message("record"), _tally.Considered, _tally.Failing, _tally.Share, _tally.Examples));
}

/// <summary>
/// Every reference a field holds names a record storage holds (of the entity type named, when one is): the distinct
/// references are gathered while the records are read, and asked of storage a hundred at a time once they all are.
/// </summary>
internal sealed class ResolvesEvaluator : Evaluator
{
    /// <summary>The most distinct references one assertion looks up.</summary>
    public const int MaxReferences = 200_000;

    private readonly ValueAssertion _assertion;
    private readonly Dictionary<string, (string FirstRecord, long Carried)> _references = new(StringComparer.Ordinal);
    private readonly List<AssertionExample> _malformed = [];
    private long _malformedCount;
    private long _wrongTypeCount;
    private long _values;
    private long _records;
    private long _filtered;
    private bool _overflow;

    public ResolvesEvaluator(int index, ValueAssertion assertion, int examples)
        : base(index, assertion, examples)
    {
        _assertion = assertion;
    }

    public override RecordNeed Need => RecordNeed.Fields;

    public override IEnumerable<string> Paths => _assertion.Where.Select(w => w.Target.Path).Prepend(_assertion.Target.Path);

    public override void Observe(string id, JsonNode record)
    {
        if (!Selected(_assertion.Where, target => RecordValues.Select(record, target.Path)))
        {
            _filtered++;
            return;
        }

        _records++;
        foreach (var value in RecordValues.Select(record, _assertion.Target.Path))
        {
            _values++;
            if (!RecordValues.TryText(value, out var text) || !TargetId.IsRecordReference(text))
            {
                _malformedCount++;
                if (_malformed.Count < Examples)
                {
                    _malformed.Add(new AssertionExample(id, TestResults.Quote(RecordValues.Text(value)), "is not a record reference (partition:entity-type:id, with or without a version)"));
                }

                continue;
            }

            var reference = TargetId.WithoutVersion(text).TrimEnd(':');
            if (_assertion.Condition.EntityType is { } entityType && reference.Split(':') is { Length: >= 3 } parts
                && !string.Equals(parts[1], entityType, StringComparison.OrdinalIgnoreCase))
            {
                _wrongTypeCount++;
                if (_malformed.Count < Examples)
                {
                    _malformed.Add(new AssertionExample(id, TestResults.Quote(text), $"points at a {parts[1]}, not a {entityType}"));
                }

                continue;
            }

            if (_references.TryGetValue(reference, out var seen))
            {
                _references[reference] = seen with { Carried = seen.Carried + 1 };
            }
            else if (_references.Count < MaxReferences)
            {
                _references[reference] = (id, 1);
            }
            else
            {
                _overflow = true;
            }
        }
    }

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return NotRead(subject);
        }

        if (_overflow)
        {
            return AssertionOutcome.Errored(Index, Assertion,
                string.Create(CultureInfo.InvariantCulture, $"The records hold more than {MaxReferences} distinct references here; narrow the test's query so the references it looks up are fewer."));
        }

        var dangling = new List<string>();
        var gate = new object();
        var chunks = _references.Keys.Chunk(StorageRecords.Batch).ToList();
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, scope.Concurrency), CancellationToken = ct }, async (chunk, token) =>
        {
            var read = await scope.Storage.ReadAsync(chunk, ["id"], token).ConfigureAwait(false);
            lock (gate)
            {
                dangling.AddRange(read.Missing);
            }
        }).ConfigureAwait(false);

        dangling.Sort(StringComparer.Ordinal);
        var examples = _malformed.Take(Examples).ToList();
        foreach (var reference in dangling.Take(Math.Max(0, Examples - examples.Count)))
        {
            var (first, carried) = _references[reference];
            examples.Add(new AssertionExample(first, reference,
                string.Create(CultureInfo.InvariantCulture, $"names no record storage holds (or one this identity may not read); {carried} value(s) carry it")));
        }

        var carriedDangling = dangling.Sum(r => _references[r].Carried);
        var failing = carriedDangling + _malformedCount + _wrongTypeCount;
        var passed = failing == 0 && _values > 0;
        var actual = string.Create(CultureInfo.InvariantCulture,
            $"{_references.Count} distinct reference(s) in {_values} value(s) of {_records} record(s); {dangling.Count} do not resolve")
            + (_malformedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_malformedCount} value(s) are not references") : string.Empty)
            + (_wrongTypeCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_wrongTypeCount} point at another entity type") : string.Empty)
            + (_filtered > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_filtered} record(s) left out by where") : string.Empty);
        var message = _values == 0
            ? "No record held a reference here to look up."
            : string.Create(CultureInfo.InvariantCulture, $"{failing} value(s) do not resolve to a record{(_assertion.Condition.EntityType is { } type ? " of type " + type : string.Empty)}.");
        return Result(passed, actual, message, _references.Count + _malformedCount + _wrongTypeCount, failing, _values == 0 ? null : 100.0 * (_values - failing) / _values, examples);
    }
}

/// <summary>An aggregate of the values a field yields over every record read, compared.</summary>
internal sealed class AggregateEvaluator : Evaluator
{
    /// <summary>The most distinct values a distinct count keeps.</summary>
    public const int MaxDistinct = 1_000_000;

    private readonly AggregateAssertion _assertion;
    private readonly Aggregator _aggregator;
    private long _missing;
    private long _leftOut;

    public AggregateEvaluator(int index, AggregateAssertion assertion, int examples)
        : base(index, assertion, examples)
    {
        _assertion = assertion;
        _aggregator = new Aggregator(assertion.Function == AggregateFunction.Distinct);
    }

    public override RecordNeed Need => RecordNeed.Fields;

    public override IEnumerable<string> Paths => [_assertion.Target.Path];

    public override void Observe(string id, JsonNode record)
    {
        var values = RecordValues.Select(record, _assertion.Target.Path);
        if (_assertion.Except.Count > 0)
        {
            var kept = values.Where(v => !LeftOut(_assertion, v)).ToList();
            _leftOut += values.Count - kept.Count;
            values = kept;
        }

        if (values.Count == 0)
        {
            _missing++;
            return;
        }

        foreach (var value in values)
        {
            _aggregator.Add(value);
        }
    }

    /// <summary>
    /// Whether an aggregate leaves <paramref name="value"/> out: it equals one of the values its <c>except</c> lists, compared
    /// as a condition compares a value with what it expects. A value left out counts as no value.
    /// </summary>
    internal static bool LeftOut(AggregateAssertion assertion, JsonNode value)
        => assertion.Except.Count > 0 && assertion.Except.Any(e => ValueComparer.Equal(value, e, null, out _));

    /// <summary>What an aggregate's result says it left out, as a reader needs to know it.</summary>
    internal static string LeftOutNote(AggregateAssertion assertion, long leftOut) => assertion.Except.Count == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $" ({leftOut} value(s) left out: {string.Join(", ", assertion.Except)})");

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return Task.FromResult(NotRead(subject));
        }

        var (measured, why) = _aggregator.Measure(_assertion.Function, _missing, _assertion.Comparison);
        if (measured is not { } value)
        {
            return Task.FromResult(Result(false, why, why, _aggregator.Count, null));
        }

        var passed = ValueComparer.Evaluate(_assertion.Comparison, value, _assertion.Tolerance);
        var actual = $"{AssertionText.Of(_assertion.Function)}({_assertion.Target}) is {value}" + _aggregator.Note(_assertion.Function) + LeftOutNote(_assertion, _leftOut);
        var outcome = Result(passed, actual, $"{actual}; expected {_assertion.Expected}.", _aggregator.Count, passed ? 0 : 1, value.Number);
        return Task.FromResult(!passed && _assertion.WarnWithin is { } band && ValueComparer.Within(_assertion.Comparison, value, _assertion.Tolerance, band)
            ? Warned(outcome, WithinBand(band, ValueComparer.Miss(_assertion.Comparison, value, _assertion.Tolerance)))
            : outcome);
    }
}

/// <summary>
/// Adds up values for an aggregate: numbers (and text holding a number), ISO 8601 dates for a minimum or maximum, and, for a
/// distinct count, every value's text.
/// </summary>
public sealed class Aggregator
{
    private readonly HashSet<string>? _distinct;
    private double _sum;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private DateTimeOffset? _earliest;
    private DateTimeOffset? _latest;

    public Aggregator(bool distinct)
    {
        _distinct = distinct ? new HashSet<string>(StringComparer.Ordinal) : null;
    }

    /// <summary>The values added.</summary>
    public long Count { get; private set; }

    public long Numbers { get; private set; }

    public long Dates { get; private set; }

    /// <summary>Values that are neither a number nor a date, left out of a numeric aggregate.</summary>
    public long Others { get; private set; }

    public bool Overflow { get; private set; }

    public void Add(JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Count++;
        if (_distinct is not null)
        {
            if (_distinct.Count < AggregateEvaluator.MaxDistinct)
            {
                _distinct.Add(RecordValues.Text(value));
            }
            else if (!_distinct.Contains(RecordValues.Text(value)))
            {
                Overflow = true;
            }
        }

        if (RecordValues.TryNumber(value, out var number) || (RecordValues.TryText(value, out var text) && RecordValues.TryParseNumber(text, out number)))
        {
            AddNumber(number);
        }
        else if (RecordValues.TryText(value, out var date) && RecordValues.TryInstant(date, out var instant))
        {
            Dates++;
            _earliest = _earliest is { } e && e <= instant ? e : instant;
            _latest = _latest is { } l && l >= instant ? l : instant;
        }
        else
        {
            Others++;
        }
    }

    public void AddNumber(double number)
    {
        Numbers++;
        _sum += number;
        _min = Math.Min(_min, number);
        _max = Math.Max(_max, number);
    }

    /// <summary>The aggregate, or null with the reason it has none (no number to add up, too many distinct values).</summary>
    public (Measured? Value, string Why) Measure(AggregateFunction function, long missing, Comparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var dated = comparison.Terms.Any(t => t.Value.Kind == ExpectedValueKind.Text);
        switch (function)
        {
            case AggregateFunction.Count:
                return (Measured.Of(Count), string.Empty);
            case AggregateFunction.Missing:
                return (Measured.Of(missing), string.Empty);
            case AggregateFunction.Distinct:
                return Overflow
                    ? (null, string.Create(CultureInfo.InvariantCulture, $"more than {AggregateEvaluator.MaxDistinct} distinct values; narrow the test's query"))
                    : (Measured.Of(_distinct!.Count), string.Empty);
            case AggregateFunction.Min or AggregateFunction.Max when dated:
                var instant = function == AggregateFunction.Min ? _earliest : _latest;
                return instant is { } at ? (Measured.Of(at), string.Empty) : (null, "no value is an ISO 8601 date");
            default:
                if (Numbers == 0)
                {
                    return (null, "no value is a number");
                }

                return (function switch
                {
                    AggregateFunction.Sum => Measured.Of(_sum),
                    AggregateFunction.Avg => Measured.Of(_sum / Numbers),
                    AggregateFunction.Min => Measured.Of(_min),
                    _ => Measured.Of(_max),
                }, string.Empty);
        }
    }

    /// <summary>
    /// What the aggregate left out, as a reader needs to know it: the values a sum, an average, a minimum or a maximum could
    /// not take, being neither numbers nor dates. A count, a distinct count and a count of missing values take every value,
    /// so they leave nothing out and say nothing.
    /// </summary>
    public string Note(AggregateFunction function) => Others > 0 && function is AggregateFunction.Sum or AggregateFunction.Avg or AggregateFunction.Min or AggregateFunction.Max
        ? string.Create(CultureInfo.InvariantCulture, $" ({Others} of {Count} value(s) are neither numbers nor dates and were left out)")
        : string.Empty;
}

/// <summary>
/// No two records share the values of the fields named, taken together. It keeps one entry per record it observes, and a test
/// observes at most its maxRecords (at most <see cref="AssertionDefaults.MaxRecordsCeiling"/>), so it holds no more entries
/// than a distinct count holds values (<see cref="AggregateEvaluator.MaxDistinct"/>); a distinct count needs a cap of its own
/// because one record yields a value for every element of an array it crosses.
/// </summary>
internal sealed class UniqueEvaluator : Evaluator
{
    private const string Separator = "\u001f";

    private readonly UniqueAssertion _assertion;
    private readonly Dictionary<string, (long Count, List<string> Ids, string Shown)> _keys = new(StringComparer.Ordinal);
    private long _records;
    private long _empty;

    public UniqueEvaluator(int index, UniqueAssertion assertion, int examples)
        : base(index, assertion, examples)
    {
        _assertion = assertion;
    }

    public override RecordNeed Need => RecordNeed.Fields;

    public override IEnumerable<string> Paths => _assertion.Fields;

    public override void Observe(string id, JsonNode record)
    {
        var parts = _assertion.Fields.Select(f => RecordValues.Select(record, f)).ToList();
        if (parts.All(p => p.Count == 0))
        {
            _empty++;
            return;
        }

        _records++;
        var key = string.Join(Separator, parts.Select(p => string.Join(",", p.Select(v => CanonicalJson.ToString(v)))));
        if (_keys.TryGetValue(key, out var seen))
        {
            if (seen.Ids.Count < 5)
            {
                seen.Ids.Add(id);
            }

            _keys[key] = seen with { Count = seen.Count + 1 };
        }
        else
        {
            _keys[key] = (1, [id], string.Join(" + ", parts.Select(RecordValues.Describe)));
        }
    }

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return Task.FromResult(NotRead(subject));
        }

        var shared = _keys.Values.Where(k => k.Count > 1).OrderByDescending(k => k.Count).ToList();
        var failing = shared.Sum(k => k.Count);
        var examples = shared.Take(Examples)
            .Select(k => new AssertionExample(string.Join(", ", k.Ids) + (k.Count > k.Ids.Count ? ", ..." : string.Empty), TestResults.Quote(k.Shown),
                string.Create(CultureInfo.InvariantCulture, $"{k.Count} records share it")))
            .ToList();
        var actual = string.Create(CultureInfo.InvariantCulture, $"{_records} record(s), {_keys.Count} distinct; {shared.Count} value(s) shared by more than one record")
            + (_empty > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_empty} with none of the fields passed over") : string.Empty);
        return Task.FromResult(Result(
            shared.Count == 0 && _records > 0,
            actual,
            _records == 0 ? "No record held any of the fields." : string.Create(CultureInfo.InvariantCulture, $"{failing} record(s) share their values with another."),
            _records,
            failing,
            shared.Count,
            examples));
    }
}

/// <summary>
/// The records grouped by the distinct values of a field, as the search's aggregation answers it: the groups expected with
/// their counts, those that must not be there, and how many groups there are.
/// </summary>
internal sealed class GroupEvaluator(int index, GroupAssertion assertion, int examples) : Evaluator(index, assertion, examples)
{
    /// <summary>The groups the outcome names in its actual.</summary>
    private const int Shown = 10;

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        var field = AggregationField(SearchField(assertion.Field), scope.Template);
        var (_, buckets) = await scope.Search.AggregateAsync(scope.Query with { ReturnedFields = ["id"] }, field, ct).ConfigureAwait(false);
        // A group the service named no key for (a plain number's) is shown as null, the way its JSON reads.
        var found = buckets.GroupBy(b => b.Key ?? "null", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(b => b.Count), StringComparer.Ordinal);
        var failures = new List<AssertionExample>();
        foreach (var group in assertion.Groups)
        {
            if (!found.TryGetValue(group.Key, out var count))
            {
                failures.Add(new AssertionExample(group.Key, null, $"no record is in this group; expected {group.Count}"));
            }
            else if (!ValueComparer.Evaluate(group.Count, Measured.Of(count), null))
            {
                failures.Add(new AssertionExample(group.Key, count.ToString(CultureInfo.InvariantCulture), $"holds {count} record(s); expected {group.Count}"));
            }
        }

        if (assertion.Exact)
        {
            foreach (var (key, count) in found.Where(f => !assertion.Groups.Any(g => string.Equals(g.Key, f.Key, StringComparison.Ordinal))))
            {
                failures.Add(new AssertionExample(key, count.ToString(CultureInfo.InvariantCulture), "is a group the assertion does not expect"));
            }
        }

        foreach (var key in assertion.Absent.Where(found.ContainsKey))
        {
            failures.Add(new AssertionExample(key, found[key].ToString(CultureInfo.InvariantCulture), "is a group, and is expected to be absent"));
        }

        var countFails = assertion.GroupCount is { } groupCount && !ValueComparer.Evaluate(groupCount, Measured.Of(found.Count), null);
        var actual = string.Create(CultureInfo.InvariantCulture, $"{found.Count} group(s)")
            + (found.Count == 0 ? string.Empty : ": " + string.Join(", ", found.OrderByDescending(f => f.Value).Take(Shown).Select(f => $"{f.Key} ({f.Value})"))
                + (found.Count > Shown ? ", ..." : string.Empty));
        var message = string.Join(" ", new[]
        {
            failures.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{failures.Count} group(s) differ from what is expected.") : null,
            countFails ? $"There are {found.Count} group(s); expected {assertion.GroupCount}." : null,
        }.Where(m => m is not null));
        return Result(failures.Count == 0 && !countFails, actual, message, found.Count, failures.Count + (countFails ? 1 : 0), found.Count, failures.Take(Examples).ToList());
    }

    /// <summary>
    /// A record path as the search names a field: its property names joined by dots, arrays not stepped into by name there
    /// (<c>data.Curves.CurveID</c> for <c>data.Curves[*].CurveID</c>), and a name in brackets written after its dot, as the
    /// index names a tag (<c>tags.lra.dataID</c> for <c>tags['lra.dataID']</c>).
    /// </summary>
    internal static string SearchField(string path) => RecordPathNotation.SearchName(path) ?? path;

    /// <summary>
    /// The field the search aggregates (<see cref="OsduField.AggregateBy"/>): a property of data the schema has indexed as
    /// text is aggregated by its keyword sub-field, since the search refuses to aggregate text ("Aggregations are not
    /// supported for one or more of the specified fields"), and a property of a nested array through the service's
    /// <c>nested(path, field)</c> form. Every other field, the record's own properties among them, is aggregated as it is
    /// named.
    /// </summary>
    internal static string AggregationField(string path, OsduTemplate? template)
    {
        if (template is null || !path.StartsWith("data.", StringComparison.Ordinal))
        {
            return path;
        }

        // The form aggregateBy names the field by: the keyword sub-field of text, and the service's nested(path, field) form
        // for a property of a nested array, which a plain dotted path into the array aggregates nothing of.
        var (field, _) = SearchFields.Classify(template.Schema, path);
        return field?.AggregateBy ?? path;
    }
}

/// <summary>
/// The records projected onto columns, compared as a set of rows with the rows expected. Values compare exactly as JSON, so
/// 5 and "5" differ; a field with no value is null, and one with several is the list of them.
/// </summary>
internal sealed class RecordSetEvaluator : Evaluator
{
    private readonly RecordSetAssertion _assertion;
    private readonly List<string> _expected;
    private readonly Dictionary<string, (long Count, string FirstId)> _actual = new(StringComparer.Ordinal);
    private readonly List<(string Key, string Id)> _ordered = [];
    private long _rows;

    public RecordSetEvaluator(int index, RecordSetAssertion assertion, int examples)
        : base(index, assertion, examples)
    {
        _assertion = assertion;
        _expected = assertion.Rows.Select(row => Key(row.Select(Cell).ToList())).ToList();
    }

    public override RecordNeed Need => RecordNeed.Fields;

    public override IEnumerable<string> Paths => _assertion.Columns;

    public override void Observe(string id, JsonNode record)
    {
        _rows++;
        var key = Key(_assertion.Columns.Select(c => Cell(RecordValues.Select(record, c))).ToList());
        switch (_assertion.Mode)
        {
            case RecordSetMode.Ordered:
                if (_ordered.Count <= _expected.Count)
                {
                    _ordered.Add((key, id));
                }

                break;
            case RecordSetMode.Includes or RecordSetMode.Excludes when !_expected.Contains(key, StringComparer.Ordinal):
                break;
            default:
                _actual[key] = _actual.TryGetValue(key, out var seen) ? seen with { Count = seen.Count + 1 } : (1, id);
                break;
        }
    }

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return Task.FromResult(NotRead(subject));
        }

        var expectedCounts = _expected.GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        var failures = new List<AssertionExample>();
        long failing = 0;
        switch (_assertion.Mode)
        {
            case RecordSetMode.Ordered:
                for (var i = 0; i < Math.Max(_expected.Count, _ordered.Count); i++)
                {
                    var want = i < _expected.Count ? _expected[i] : null;
                    var got = i < _ordered.Count ? _ordered[i] : default;
                    if (want is not null && got.Key is not null && string.Equals(want, got.Key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    failing++;
                    failures.Add(want is null
                        ? new AssertionExample(got.Id, TestResults.Quote(got.Key ?? string.Empty), string.Create(CultureInfo.InvariantCulture, $"row {i + 1} is not expected: only {_expected.Count} row(s) are"))
                        : got.Key is null
                            ? new AssertionExample(null, TestResults.Quote(want), string.Create(CultureInfo.InvariantCulture, $"row {i + 1} is expected, and the records end before it"))
                            : new AssertionExample(got.Id, TestResults.Quote(got.Key), string.Create(CultureInfo.InvariantCulture, $"row {i + 1} is expected to be {TestResults.Quote(want)}")));
                }

                break;
            case RecordSetMode.Excludes:
                foreach (var (key, (count, id)) in _actual)
                {
                    failing += count;
                    failures.Add(new AssertionExample(id, TestResults.Quote(key), string.Create(CultureInfo.InvariantCulture, $"is a row that must not be there; {count} record(s) hold it")));
                }

                break;
            default:
                foreach (var (key, want) in expectedCounts)
                {
                    var got = _actual.TryGetValue(key, out var seen) ? seen.Count : 0;
                    if (got < want)
                    {
                        failing += want - got;
                        failures.Add(new AssertionExample(null, TestResults.Quote(key), string.Create(CultureInfo.InvariantCulture, $"is expected {want} time(s) and found {got}")));
                    }
                }

                if (_assertion.Mode == RecordSetMode.Exact)
                {
                    foreach (var (key, (count, id)) in _actual)
                    {
                        var want = expectedCounts.TryGetValue(key, out var w) ? w : 0;
                        if (count > want)
                        {
                            failing += count - want;
                            failures.Add(new AssertionExample(id, TestResults.Quote(key), string.Create(CultureInfo.InvariantCulture, $"is found {count} time(s) and expected {want}")));
                        }
                    }
                }

                break;
        }

        var actual = string.Create(CultureInfo.InvariantCulture, $"{_rows} record(s) read against {_expected.Count} row(s) expected");
        return Task.FromResult(Result(
            failing == 0,
            actual,
            string.Create(CultureInfo.InvariantCulture, $"{failing} row(s) differ from the rows expected ({AssertionText.Of(_assertion.Mode)})."),
            _rows,
            failing,
            failing,
            failures.Take(Examples).ToList()));
    }

    private static JsonNode? Cell(IReadOnlyList<JsonNode> values) => values.Count switch
    {
        0 => null,
        1 => values[0].DeepClone(),
        _ => new JsonArray(values.Select(v => (JsonNode?)v.DeepClone()).ToArray()),
    };

    private static JsonNode? Cell(ExpectedValue? value) => value?.Kind switch
    {
        null or ExpectedValueKind.Null => null,
        ExpectedValueKind.Number => JsonValue.Create(value.Number!.Value),
        ExpectedValueKind.Boolean => JsonValue.Create(value.Boolean!.Value),
        _ => JsonValue.Create(value.Text),
    };

    private static string Key(IReadOnlyList<JsonNode?> cells) => CanonicalJson.ToString(new JsonArray(cells.ToArray()));
}

/// <summary>Every record meets the schema of its kind, as the template the test was checked against states it.</summary>
/// <summary>
/// Every record read meets what a mapping asserts of the values a record carries (osdu/docs/reference/flow/mapping-assertions.md),
/// judged by the code a render judges a document with; the assertions each record fails are counted, the ones failed most
/// named in what the outcome found.
/// </summary>
internal sealed class MappingRulesEvaluator(int index, MappingRulesAssertion assertion, int examples) : Evaluator(index, assertion, examples)
{
    /// <summary>The assertions the outcome names as failed most.</summary>
    private const int NamedMost = 5;

    private readonly Tally _tally = new(Quantifier.All, examples);
    private readonly Dictionary<string, long> _failed = new(StringComparer.Ordinal);
    private long _judged;

    public override RecordNeed Need => RecordNeed.Fields;

    public override void Observe(string id, JsonNode record)
    {
        if (assertion.Definition is not { } mapping || record is not JsonObject obj)
        {
            _tally.Add(false, id, null, record is JsonObject ? $"mapping {assertion.Mapping} was not read" : "is not a JSON object");
            return;
        }

        var findings = MappingAssertionJudge.OfStored(obj, mapping, out var notes);
        if (findings is null)
        {
            _tally.Add(false, id, null, notes.Count > 0 ? notes[0] : $"is not of the kind mapping {assertion.Mapping} renders");
            return;
        }

        _judged += findings.Checked;
        if (findings.Failed == 0)
        {
            _tally.Add(true, id, null, string.Empty);
            return;
        }

        foreach (var failed in findings.Failures.Select(f => $"{f.At} \"{f.Assertion}\"").Distinct(StringComparer.Ordinal))
        {
            _failed[failed] = _failed.GetValueOrDefault(failed) + 1;
        }

        var first = findings.Failures[0];
        _tally.Add(false, id, TestResults.Quote(first.Value),
            $"{first.Path} fails \"{first.Assertion}\": {first.Message}"
            + (findings.Failed > 1 ? string.Create(CultureInfo.InvariantCulture, $" (and {findings.Failed - 1} more)") : string.Empty));
    }

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return Task.FromResult(NotRead(subject));
        }

        var most = _failed.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(NamedMost).ToList();
        var actual = _tally.Actual("record")
            + string.Create(CultureInfo.InvariantCulture, $"; {_judged} judgement(s) made")
            + (most.Count > 0 ? "; failed most: " + string.Join(", ", most.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Key} ({f.Value})"))) : string.Empty);
        return Task.FromResult(Result(_tally.Passed, actual, _tally.Message("record"), _tally.Considered, _tally.Failing, _tally.Share, _tally.Examples));
    }
}

internal sealed class ConformsEvaluator(int index, ConformsAssertion assertion, TestScope scope) : Evaluator(index, assertion, scope.Examples)
{
    private readonly Tally _tally = new(Quantifier.All, scope.Examples);

    public override RecordNeed Need => RecordNeed.Fields;

    public override void Observe(string id, JsonNode record)
    {
        var schema = scope.Template?.Schema;
        if (schema is null || record is not JsonObject obj)
        {
            _tally.Add(false, id, null, record is JsonObject ? "the template of its kind is not at hand" : "is not a JSON object");
            return;
        }

        // The record is checked whole by the check every validation shares; a part it could not check is not a failure here,
        // as an assertion states what the record breaks, not what could not be looked at.
        var findings = RecordValidator.Check(obj, SchemaRules.Of(schema), form: RecordForm.Stored);
        var problems = findings.Problems.ToList();
        var count = findings.ProblemCount;
        if (obj["kind"] is JsonValue kind && kind.TryGetValue<string>(out var text) && !string.Equals(text, scope.Test.Kind, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new SchemaFinding("kind", "kind", "const", $"is of kind {text}, not {scope.Test.Kind}", text));
            count++;
        }

        if (count == 0)
        {
            _tally.Add(true, id, null, string.Empty);
            return;
        }

        var first = problems[0];
        _tally.Add(false, id, TestResults.Quote(first.Value),
            $"{ValidationVerdict.Where(first with { At = first.Path.Length > 0 ? first.Path : first.At })}: {first.Message}"
            + (count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (and {count - 1} more)") : string.Empty));
    }

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
        => Task.FromResult(subject.NotRead is not null
            ? NotRead(subject)
            : Result(_tally.Passed, _tally.Actual("record"), _tally.Message("record"), _tally.Considered, _tally.Failing, _tally.Share, _tally.Examples));
}

/// <summary>
/// The search indexed every record the test matches cleanly: none carries an index status above 200, the indexer's status of
/// a record it mapped whole (osdu/specs/core/INTEGRATION.md: 200 all OK, 400 a field could not be mapped, 404 schema missing).
/// </summary>
internal sealed class IndexedEvaluator(int index, IndexedAssertion assertion, int examples) : Evaluator(index, assertion, examples)
{
    /// <summary>What finds a record the indexer could not index fully: a status of 201 or above (400 mapping, 404 schema missing).</summary>
    public const string Unclean = "index.statusCode:[201 TO *]";

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        var query = scope.Query with
        {
            Query = string.IsNullOrWhiteSpace(scope.Query.Query) ? Unclean : $"({scope.Query.Query}) AND {Unclean}",
            Sort = [],
        };
        var (total, hits) = await scope.Search.FirstAsync(query with { ReturnedFields = ["id", "index"] }, Math.Max(1, Examples), ct).ConfigureAwait(false);
        var examples = hits.Select(hit => new AssertionExample(
                hit["id"]?.GetValue<string>(),
                hit["index"]?["statusCode"]?.ToJsonString(),
                hit["index"]?["trace"] is JsonArray trace && trace.Count > 0 ? TestResults.Quote(RecordValues.Text(trace[0])) : "indexed with a status above 200"))
            .ToList();
        var actual = string.Create(CultureInfo.InvariantCulture, $"{total} record(s) indexed with a status above 200")
            + (subject.Matched is { } matched ? string.Create(CultureInfo.InvariantCulture, $", of {matched} matched") : string.Empty);
        return Result(total == 0, actual, string.Create(CultureInfo.InvariantCulture, $"{total} record(s) are in the index with a status above 200: the indexer could not map them whole."), subject.Matched, total, total, examples);
    }
}

/// <summary>Every legal tag the records carry is valid now, as the legal service answers it.</summary>
internal sealed class LegalEvaluator(int index, LegalAssertion assertion, int examples) : Evaluator(index, assertion, examples)
{
    /// <summary>The most distinct legal tags one assertion asks about.</summary>
    public const int MaxTags = 10_000;

    private readonly Dictionary<string, (string FirstRecord, long Carried)> _tags = new(StringComparer.Ordinal);
    private readonly List<AssertionExample> _untagged = [];
    private long _untaggedCount;
    private long _records;
    private bool _overflow;

    public override RecordNeed Need => RecordNeed.Fields;

    public override IEnumerable<string> Paths => ["legal.legaltags"];

    public override void Observe(string id, JsonNode record)
    {
        _records++;
        var tags = RecordValues.Select(record, "legal.legaltags[*]").Select(RecordValues.Text).Where(t => t.Length > 0).ToList();
        if (tags.Count == 0)
        {
            _untaggedCount++;
            if (_untagged.Count < Examples)
            {
                _untagged.Add(new AssertionExample(id, null, "carries no legal tag"));
            }

            return;
        }

        foreach (var tag in tags)
        {
            if (_tags.TryGetValue(tag, out var seen))
            {
                _tags[tag] = seen with { Carried = seen.Carried + 1 };
            }
            else if (_tags.Count < MaxTags)
            {
                _tags[tag] = (id, 1);
            }
            else
            {
                _overflow = true;
            }
        }
    }

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return NotRead(subject);
        }

        if (_overflow)
        {
            return AssertionOutcome.Errored(Index, Assertion, string.Create(CultureInfo.InvariantCulture, $"The records carry more than {MaxTags} distinct legal tags; narrow the test's query."));
        }

        var invalid = await scope.Legal.InvalidAsync(_tags.Keys, ct).ConfigureAwait(false);
        var examples = _untagged.ToList();
        foreach (var (tag, reason) in invalid.OrderBy(i => i.Key, StringComparer.Ordinal).Take(Math.Max(0, Examples - examples.Count)))
        {
            var (first, carried) = _tags[tag];
            examples.Add(new AssertionExample(first, tag, string.Create(CultureInfo.InvariantCulture, $"{reason}; {carried} record(s) carry it")));
        }

        var failing = invalid.Keys.Sum(t => _tags[t].Carried) + _untaggedCount;
        var actual = string.Create(CultureInfo.InvariantCulture, $"{_tags.Count} legal tag(s) on {_records} record(s); {invalid.Count} invalid")
            + (_untaggedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {_untaggedCount} record(s) carry none") : string.Empty);
        return Result(failing == 0 && _records > 0, actual,
            _records == 0 ? "No record was there to check." : string.Create(CultureInfo.InvariantCulture, $"{failing} record(s) carry an invalid legal tag, or none."),
            _tags.Count, invalid.Count + _untaggedCount, invalid.Count, examples);
    }
}

/// <summary>
/// Every record a delivery flow's ledger holds as delivered in the partition is among the records the test matches; with
/// exact, the test matches no record the ledger does not hold either.
/// </summary>
internal sealed class DeliveredEvaluator(int index, DeliveredAssertion assertion, int examples, AssertionTest test) : Evaluator(index, assertion, examples)
{
    private readonly HashSet<string> _found = new(StringComparer.Ordinal);

    public override RecordNeed Need => RecordNeed.Ids;

    public override void Observe(string id, JsonNode record) => _found.Add(id);

    public override async Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return NotRead(subject);
        }

        if (subject.Sampled)
        {
            return AssertionOutcome.Errored(Index, Assertion,
                "The test read a sample of the records its query matches, and whether every delivered record is among them needs all of them: raise maxRecords, or narrow the query.");
        }

        if (scope.Ledger is not { } ledger)
        {
            return AssertionOutcome.Errored(Index, Assertion, DeliveryServices.NoLedgerMessage);
        }

        var ledgers = (await ledger.ListLedgersAsync(scope.Partition, ct).ConfigureAwait(false))
            .Where(l => l.Kind == LedgerKinds.Delivery && string.Equals(l.FlowName, assertion.Flow, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var named = assertion.Interface is { } wanted
            ? ledgers.Where(l => string.Equals(l.Interface, wanted, StringComparison.OrdinalIgnoreCase)).ToList()
            : ledgers;
        if (named.Count == 0)
        {
            return AssertionOutcome.Errored(Index, Assertion, ledgers.Count == 0
                ? $"Delivery flow '{assertion.Flow}' keeps no ledger in partition '{scope.Partition}': it has not delivered there, or is named differently."
                : $"Delivery flow '{assertion.Flow}' has no interface '{assertion.Interface}' in partition '{scope.Partition}'; its interfaces are {string.Join(", ", ledgers.Select(l => l.Interface))}.");
        }

        if (named.Count > 1)
        {
            return AssertionOutcome.Errored(Index, Assertion,
                $"Delivery flow '{assertion.Flow}' delivers {named.Count} interfaces in partition '{scope.Partition}' ({string.Join(", ", named.Select(l => l.Interface))}); name the one whose records this test reads with interface.");
        }

        var delivered = new Dictionary<string, string>(StringComparer.Ordinal);
        DeliveryKey? after = null;
        while (true)
        {
            var page = await ledger.ListRecordsAsync(named[0].FlowId, after, 1000, ct).ConfigureAwait(false);
            foreach (var record in page)
            {
                if (record.Status == RecordStatus.Delivered && record.TargetId is { Length: > 0 } target)
                {
                    delivered[TargetId.WithoutVersion(target).TrimEnd(':')] = record.Label ?? record.SourceKey;
                }
            }

            if (delivered.Count > test.MaxRecords)
            {
                return AssertionOutcome.Errored(Index, Assertion, string.Create(CultureInfo.InvariantCulture,
                    $"The ledger of '{assertion.Flow}' holds more than {test.MaxRecords} delivered records, more than this test reads; raise maxRecords (at most {AssertionDefaults.MaxRecordsCeiling})."));
            }

            if (page.Count < 1000)
            {
                break;
            }

            after = page[^1].DeliveryKey;
        }

        var missing = delivered.Keys.Where(id => !_found.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var extra = assertion.Exact ? _found.Where(id => !delivered.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList() : [];
        var failures = missing.Select(id => new AssertionExample(id, TestResults.Quote(delivered[id]), $"is delivered by {assertion.Flow}, and the test's query does not find it"))
            .Concat(extra.Select(id => new AssertionExample(id, null, $"is found by the test's query, and {assertion.Flow} did not deliver it")))
            .Take(Examples)
            .ToList();
        var actual = string.Create(CultureInfo.InvariantCulture, $"{delivered.Count} delivered, {_found.Count} found; {missing.Count} delivered record(s) not found")
            + (assertion.Exact ? string.Create(CultureInfo.InvariantCulture, $", {extra.Count} found and not delivered") : string.Empty);
        return Result(missing.Count == 0 && extra.Count == 0, actual,
            string.Create(CultureInfo.InvariantCulture, $"{missing.Count + extra.Count} record(s) differ between the ledger of {assertion.Flow} and what OSDU holds."),
            delivered.Count, missing.Count + extra.Count, missing.Count, failures);
    }
}
