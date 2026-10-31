using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>
/// Adds up what a check meets, row by row in the order the scope is read: each variable's outcomes, its findings grouped by
/// reason, the values behind them and the first example records, and what the rows came to as records. Counts are exact;
/// what is listed is capped by <see cref="ValueCheckLimits"/>, so the tally stays the same size however many rows fail.
/// </summary>
internal sealed partial class ValueTally
{
    /// <summary>How bad an outcome is for a row: the worst of a variable's outcomes in a row is what the row counts as.</summary>
    private enum Standing
    {
        NotApplicable,
        Valid,
        Empty,
        Invalid,
        Held,
    }

    private readonly SchemaSnapshot _schema;
    private readonly ValueCheckRequest _request;
    private readonly ValueCheckLimits _limits;
    private readonly Action<ValueCheckOccurrence>? _each;
    private readonly Dictionary<string, VariableTally> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _itemTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _childDatasets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Count, List<ValueCheckSample> Samples)> _passedOver = new(StringComparer.Ordinal);
    private readonly List<ValueCheckSample> _keyless = [];
    private long _checked;
    private long _passed;
    private long _keylessCount;
    private long _clean;
    private long _withHeld;
    private long _withInvalid;
    private long _withEmpty;
    private long _withFailedAssertion;
    private long _heldByAssertion;
    private readonly FlowSystemColumns _system;
    private SourceRecord? _row;

    public ValueTally(
        MappingDefinition mapping, SchemaSnapshot schema, EntrySelection selection, ValueCheckRequest request, ValueCheckLimits limits, Action<ValueCheckOccurrence>? each,
        FlowSystemColumns? system = null)
    {
        _system = system ?? new FlowSystemColumns();
        _schema = schema;
        _request = request;
        _limits = limits;
        _each = each;

        // Every variable the check evaluates is listed, in the order the mapping writes them, whether or not a row fails it.
        foreach (var entry in mapping.Entries)
        {
            var target = entry.Target.Text;
            var evaluated = entry.IsRepeater ? selection.Covers(target) : selection.Includes(target);
            if (evaluated && !_variables.ContainsKey(target))
            {
                _variables[target] = new VariableTally(target, entry, entry.Target.Repeater?.Text, limits);
            }

            if (entry.IsRepeater)
            {
                _itemTargets[target] = mapping.ItemEntries(entry).Select(e => e.Target.Text).ToHashSet(StringComparer.Ordinal);
                if (entry.Source?.Child is { } child)
                {
                    _childDatasets[target] = child;
                }
            }
        }

        foreach (var name in schema.RequiredAt("data"))
        {
            var target = $"{TemplatePath.Prefix}.data.{name}";
            if (selection.Covers(target) && !_variables.ContainsKey(target))
            {
                _variables[target] = new VariableTally(target, null, null, limits);
            }
        }
    }

    /// <summary>Counts a row a delivery never renders, by why.</summary>
    public void PassOver(string reason, SourceRecord record, MappingRenderer renderer)
    {
        _passed++;
        if (!_passedOver.TryGetValue(reason, out var passed))
        {
            passed = (0, []);
        }

        if (passed.Samples.Count < _limits.MaxPassedOverSamples)
        {
            passed.Samples.Add(Sample(record, renderer, null) with { Message = reason });
        }

        _passedOver[reason] = (passed.Count + 1, passed.Samples);
    }

    /// <summary>Adds one inspected row: each outcome to its variable, and what the row comes to as a record.</summary>
    public void Add(SourceRecord record, RecordInspection inspection, MappingRenderer renderer)
    {
        var row = _checked++;
        _row = record;
        var basis = Sample(record, renderer, inspection);
        if (inspection.Key is null)
        {
            _keylessCount++;
            if (_keyless.Count < _request.Samples)
            {
                _keyless.Add(basis with { Message = "a part of the record key is empty, so the record cannot be tracked and a run holds it" });
            }
        }

        var worst = new Dictionary<string, Standing>(StringComparer.Ordinal);
        foreach (var outcome in inspection.Outcomes)
        {
            if (!_variables.TryGetValue(outcome.Target, out var variable))
            {
                // An entry reaching the variables named from above (a list written whole that holds one) is listed as met.
                variable = new VariableTally(outcome.Target, outcome.Entry, outcome.Entry?.Target.Repeater?.Text, _limits);
                _variables[outcome.Target] = variable;
            }

            var standing = Judge(outcome, variable, row, basis);
            if (outcome.Item is not null)
            {
                variable.CountItem(standing);
            }

            worst[outcome.Target] = worst.TryGetValue(outcome.Target, out var seen) && seen > standing ? seen : standing;
        }

        // What the assertions found is noted on the variable each judged, once per row whatever the values that failed.
        var findings = inspection.Assertions;
        foreach (var failure in findings?.Failures ?? [])
        {
            if (_variables.TryGetValue($"{TemplatePath.Prefix}.{failure.At}", out var asserted))
            {
                var sample = ItemOf(failure, inspection) is { } item ? basis with { Item = item + 1 } : basis;
                Note(asserted, "asserted", asserted.Target, failure.Assertion, Fails(failure), Clip(failure.Value, _limits.MaxValueChars), row, sample, failure.Message, failure.OnFail);
                asserted.CountAsserted(row, failure.Path);
            }
        }

        _withFailedAssertion += findings is { Failed: > 0 } ? 1 : 0;
        _heldByAssertion += findings?.Holds == true ? 1 : 0;

        bool held = false, invalid = false, empty = false;
        foreach (var variable in _variables.Values)
        {
            // A variable of data the schema requires that no entry fills directly speaks only when the record lacks it.
            var standing = worst.TryGetValue(variable.Target, out var met) ? met : variable.Entry is null ? Standing.Valid : Standing.NotApplicable;
            variable.CountRow(standing);
            held |= standing == Standing.Held;
            invalid |= standing == Standing.Invalid;
            empty |= standing == Standing.Empty;
        }

        _withHeld += held ? 1 : 0;
        _withInvalid += invalid ? 1 : 0;
        _withEmpty += empty ? 1 : 0;
        _clean += held || invalid || empty || findings is { Failed: > 0 } ? 0 : 1;
    }

    public ValueCheckRows Rows(long scopeRecords, long read, bool complete) => new()
    {
        ScopeRecords = scopeRecords,
        Read = read,
        Checked = _checked,
        Complete = complete,
        PassedOver = _passed,
        PassedOverWhy = _passedOver
            .OrderByDescending(p => p.Value.Count)
            .Select(p => new ValueCheckPassedOver(p.Key, p.Value.Count, p.Value.Samples))
            .ToList(),
        Keyless = _keylessCount,
        KeylessSamples = _keyless,
        Clean = _clean,
        WithHeld = _withHeld,
        WithInvalid = _withInvalid,
        WithEmpty = _withEmpty,
        WithFailedAssertion = _withFailedAssertion,
        HeldByAssertion = _heldByAssertion,
    };

    public IReadOnlyList<ValueCheckVariable> Variables() => _variables.Values.Select(a => a.Build(_request.SkipSamples)).ToList();

    /// <summary>
    /// What one outcome amounts to, noted where it belongs: a value is held to its template's rules and counted among the
    /// values or the findings, and every other outcome is a finding with its reason. A search still unanswered holds.
    /// </summary>
    private Standing Judge(EntryOutcome outcome, VariableTally variable, long row, ValueCheckSample basis)
    {
        var sample = outcome.Item is { } item ? basis with { Item = item + 1 } : basis;
        switch (outcome.Kind)
        {
            case EntryOutcomeKind.Value:
                var problems = Problems(outcome);
                if (problems.Count == 0)
                {
                    variable.CountValue(Text(outcome.Value, _limits.MaxValueChars));
                    return Standing.Valid;
                }

                foreach (var problem in problems.DistinctBy(p => (p.At, p.Rule, p.Message)))
                {
                    Note(variable, "invalid", problem.At, problem.Rule, problem.Message, Clip(problem.Value, _limits.MaxValueChars), row, sample);
                }

                return Standing.Invalid;
            case EntryOutcomeKind.Held:
            case EntryOutcomeKind.Waiting:
                foreach (var reason in outcome.Reasons.Distinct(StringComparer.Ordinal))
                {
                    var said = Said(reason, outcome.Target);
                    Note(variable, "held", outcome.Target, null, said, Cause(said), row, sample);
                }

                return Standing.Held;
            case EntryOutcomeKind.Empty:
                var why = outcome.Reasons.Count > 0 ? Said(outcome.Reasons[0], outcome.Target) : "gives no value";
                Note(variable, "empty", outcome.Target, null, why, Cause(why), row, sample);
                return Standing.Empty;
            default:
                var because = outcome.Reasons.Count > 0 ? Said(outcome.Reasons[0], outcome.Target) : "the mapping means no value for the row";
                Note(variable, "notApplicable", outcome.Target, null, because, null, row, sample);
                return Standing.NotApplicable;
        }
    }

    /// <summary>
    /// What a written value breaks of its template. The array a repeater writes is checked for what only the array says (its
    /// length, the properties each item must have); what each property of its items is written with is its own entry's.
    /// </summary>
    private IReadOnlyList<ValueProblem> Problems(EntryOutcome outcome)
    {
        if (outcome.Entry is not { } entry)
        {
            return [];
        }

        var problems = TemplateValueRules.Check(outcome.Value, entry.Target, _schema);
        return entry.IsRepeater && _itemTargets.TryGetValue(entry.Target.Text, out var items)
            ? problems.Where(p => p.Rule == "required" || !items.Contains(p.At)).ToList()
            : problems;
    }

    /// <summary>
    /// Notes one occurrence of a finding on a variable, with the record it is in. The record's message is
    /// <paramref name="said"/> when given (why this one value fails an assertion), else the finding's message. Each
    /// occurrence is also passed to the listing of failing rows, except a value the mapping means to leave empty. A finding
    /// of an assertion carries what its failure does, <paramref name="onFail"/>.
    /// </summary>
    private void Note(
        VariableTally variable, string outcome, string at, string? rule, string message, string? value, long row, ValueCheckSample sample, string? said = null, string? onFail = null)
    {
        var child = variable.Repeater is { } repeater ? _childDatasets.GetValueOrDefault(repeater) : null;
        var reason = said ?? message;
        var childRow = ChildRow(_row, child, sample.Item);
        var record = sample with
        {
            Value = value,
            Message = reason,
            File = _system.FileName is { } fileColumn ? childRow?.GetString(fileColumn) ?? sample.File : sample.File,
            Row = _system.RowNumber is { } rowColumn && long.TryParse(childRow?.GetString(rowColumn), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number
                : sample.Row,
            Columns = ColumnsToFix(variable.Reads, reason, value, sample.Item, child),
        };
        variable.Note(outcome, at, rule, message, value, row, record, _request, onFail);

        // What the mapping means no value for is no failure, so a listing of the failing rows leaves it out.
        if (_each is not null && outcome != "notApplicable")
        {
            _each(new ValueCheckOccurrence
            {
                Target = variable.Target,
                At = at,
                Outcome = outcome,
                Rule = rule,
                Message = said ?? message,
                Value = value,
                Record = record,
                Source = _row,
                Child = child,
            });
        }
    }

    /// <summary>What a row holds in a source column: for an item of <paramref name="child"/>, the child row it was written from, counting from 1.</summary>
    internal static string? CellOf(DatasetColumn column, SourceRecord? source, int? item, string? child)
    {
        if (source is null)
        {
            return null;
        }

        return column.Child is null
            ? source.Row.GetString(column.Column)
            : string.Equals(column.Child, child, StringComparison.OrdinalIgnoreCase) ? ChildRow(source, child, item)?.GetString(column.Column) : null;
    }

    private static SourceRow? ChildRow(SourceRecord? source, string? child, int? item)
    {
        if (source is null || child is null || item is not { } number)
        {
            return null;
        }

        var rows = source.ScopeRows(child);
        return number >= 1 && number <= rows.Count ? rows[number - 1] : null;
    }

    /// <summary>
    /// The source columns behind a value that went wrong, what connects the reason to the file: the ones the reason names,
    /// else the ones holding the value, else the only column the variable reads, else those it reads that are empty in this
    /// row. Null when none of these says which, rather than a guess.
    /// </summary>
    private IReadOnlyList<string>? ColumnsToFix(IReadOnlyList<DatasetColumn> reads, string reason, string? value, int? item, string? child)
    {
        if (reads.Count == 0)
        {
            return null;
        }

        var named = reads.Where(column => Names(reason, column.ToString())).ToList();
        if (named.Count > 0)
        {
            return named.ConvertAll(column => column.ToString());
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            var holding = reads.Where(column => string.Equals(CellOf(column, _row, item, child)?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (holding.Count > 0)
            {
                return holding.ConvertAll(column => column.ToString());
            }
        }

        if (reads.Count == 1)
        {
            return [reads[0].ToString()];
        }

        var blank = reads.Where(column => column.Child is null || item is not null).Where(column => string.IsNullOrWhiteSpace(CellOf(column, _row, item, child))).ToList();
        return blank.Count > 0 ? blank.ConvertAll(column => column.ToString()) : null;
    }

    /// <summary>Whether <paramref name="reason"/> names a column: its whole name, not the start of a longer one.</summary>
    private static bool Names(string reason, string column)
    {
        for (var at = reason.IndexOf(column, StringComparison.OrdinalIgnoreCase); at >= 0; at = reason.IndexOf(column, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + column.Length;
            if (end == reason.Length || !(char.IsLetterOrDigit(reason[end]) || reason[end] == '_'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The child row a failed value was written from, counting from 0, or null for a failure of no one item. A value judged
    /// as the row gives it names the row; one judged in the record names its place in the array, which leaves out the rows
    /// that wrote no item, so the row is the one written at that place.
    /// </summary>
    private static int? ItemOf(Validation.AssertionFailure failure, RecordInspection inspection)
    {
        var open = failure.Path.IndexOf('[', StringComparison.Ordinal);
        var close = open < 0 ? -1 : failure.Path.IndexOf(']', open);
        if (close < 0 || !int.TryParse(failure.Path.AsSpan(open + 1, close - open - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var place))
        {
            return null;
        }

        if (failure.Path.StartsWith(DatasetColumn.Prefix + ".", StringComparison.Ordinal))
        {
            return place;
        }

        var array = $"{TemplatePath.Prefix}.{failure.Path[..open]}";
        var written = inspection.Outcomes
            .Where(o => o.Kind == EntryOutcomeKind.Value && o.Item is not null && o.Entry?.Target.Repeater?.Text == array)
            .Select(o => o.Item!.Value)
            .Distinct()
            .Order()
            .ToList();
        return place < written.Count ? written[place] : null;
    }

    private static ValueCheckSample Sample(SourceRecord record, MappingRenderer renderer, RecordInspection? inspection)
    {
        string sourceKey;
        Guid? deliveryKey;
        if (inspection is null)
        {
            var key = renderer.DeriveKey(record.Row, out var values);
            sourceKey = Identity.SourceKey.Display(renderer.Mapping.Dataset.System, values);
            deliveryKey = key?.Value;
        }
        else
        {
            sourceKey = inspection.SourceKey;
            deliveryKey = inspection.Key?.Value;
        }

        return new ValueCheckSample
        {
            SourceKey = sourceKey,
            Label = renderer.Label(record.Row),
            DeliveryKey = deliveryKey,
            File = record.Origin.FileName,
            Row = record.Origin.RowNumber,
        };
    }

    /// <summary>
    /// What a failure of an assertion amounts to, as its finding says it: the assertion and what its failure does, the same
    /// for every row it fails in, so the rows one assertion fails are one finding.
    /// </summary>
    private static string Fails(Validation.AssertionFailure failure) => failure.OnFail switch
    {
        Model.AssertionWords.Report => $"fails \"{failure.Assertion}\" ({failure.Stage}); the record is sent and the failure recorded",
        Model.AssertionWords.Omit => $"fails \"{failure.Assertion}\" ({failure.Stage}); the value is left out",
        _ => $"fails \"{failure.Assertion}\" ({failure.Stage}); the record is held before it is sent",
    };

    /// <summary>A reason without the variable it names first, which the finding already says.</summary>
    private static string Said(string reason, string target)
        => reason.StartsWith(target + ": ", StringComparison.Ordinal) ? reason[(target.Length + 2)..] : reason;

    /// <summary>The value a reason names: the first one it quotes, or the first OSDU id in it.</summary>
    private string? Cause(string reason)
    {
        var quoted = Quoted().Match(reason);
        if (quoted.Success)
        {
            return Clip(quoted.Groups["text"].Value, _limits.MaxValueChars);
        }

        var id = OsduId().Match(reason);
        return id.Success ? Clip(id.Value, _limits.MaxValueChars) : null;
    }

    /// <summary>
    /// A reason with what differs from row to row taken out (the values it quotes, the ids and the numbers it names), so
    /// the rows failing for one reason are one finding, however many values caused it.
    /// </summary>
    internal static string Shape(string reason)
        => Digits().Replace(OsduId().Replace(Quoted().Replace(reason, "'…'"), "<id>"), "#");

    /// <summary>A value as a listing names it: text as it is, anything else as its JSON, clipped.</summary>
    private static string Text(JsonNode? value, int max)
        => Clip(value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : CanonicalJson.ToString(value), max);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    [GeneratedRegex(@"'(?<text>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"[\w\-\.]+:[\w\-\.]+--[\w\-\.]+:[\w\-\.\:\%]*", RegexOptions.CultureInvariant)]
    private static partial Regex OsduId();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    /// <summary>The item a value is in, as a failure's path names it: <c>data.Curves[3]</c> of <c>data.Curves[3].Mnemonic</c>.</summary>
    [GeneratedRegex(@"^[^\[]*\[\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex ItemOfPath();

    /// <summary>One variable's counts, values and findings.</summary>
    private sealed class VariableTally(string target, MappingEntry? entry, string? repeater, ValueCheckLimits limits)
    {
        private readonly Dictionary<Standing, long> _rows = [];
        private readonly Dictionary<Standing, long> _items = [];
        private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Outcome, string At, string Shape), FindingTally> _findings = [];
        private bool _moreValues;
        private long _unlisted;
        private long _asserted;
        private long _lastAsserted = -1;
        private long _assertedItems;
        private readonly HashSet<string> _assertedInRow = new(StringComparer.Ordinal);

        public string Target => target;

        public MappingEntry? Entry => entry;

        public string? Repeater => repeater;

        /// <summary>The source columns the entry filling the variable reads.</summary>
        public IReadOnlyList<DatasetColumn> Reads { get; } = entry?.Columns.Distinct().ToList() ?? [];

        public void CountRow(Standing standing) => _rows[standing] = _rows.GetValueOrDefault(standing) + 1;

        public void CountItem(Standing standing) => _items[standing] = _items.GetValueOrDefault(standing) + 1;

        /// <summary>
        /// Counts a row a value of the variable failed an assertion in, once however many failed, and for a variable of a
        /// repeated item each item the failure at <paramref name="path"/> is in, once however many of its assertions it
        /// fails. A failure of the values together (<c>values: any</c>) names no one item, so it counts the row alone.
        /// </summary>
        public void CountAsserted(long row, string path)
        {
            if (row != _lastAsserted)
            {
                _asserted++;
                _lastAsserted = row;
                _assertedInRow.Clear();
            }

            if (repeater is not null && ItemOfPath().Match(path) is { Success: true } item && _assertedInRow.Add(item.Value))
            {
                _assertedItems++;
            }
        }

        public void CountValue(string value)
        {
            if (_values.TryGetValue(value, out var count))
            {
                _values[value] = count + 1;
            }
            else if (_values.Count < limits.TrackedValues)
            {
                _values[value] = 1;
            }
            else
            {
                _moreValues = true;
            }
        }

        public void Note(string outcome, string at, string? rule, string message, string? value, long row, ValueCheckSample record, ValueCheckRequest request, string? onFail)
        {
            var key = (outcome, at, Shape(message));
            if (!_findings.TryGetValue(key, out var finding))
            {
                if (_findings.Count >= limits.MaxFindings)
                {
                    _unlisted++;
                    return;
                }

                finding = new FindingTally(outcome, at, rule, message, onFail);
                _findings[key] = finding;
            }

            finding.Add(value, row, record, request, limits);
        }

        public ValueCheckVariable Build(long skipped) => new()
        {
            Target = target,
            Entry = entry?.Where,
            Columns = Reads.Select(c => c.ToString()).ToList(),
            Required = entry?.Required ?? true,
            Repeater = repeater,
            Rows = Counts(_rows) with { Asserted = _asserted },
            // An item failing assertions of both stages is named by the row and by the record, so the count is held to the items.
            Items = repeater is null ? null : Counts(_items) with { Asserted = Math.Min(_assertedItems, _items.Values.Sum()) },
            Values = _values
                .OrderByDescending(v => v.Value)
                .ThenBy(v => v.Key, StringComparer.Ordinal)
                .Take(limits.MaxValues)
                .Select(v => new ValueCheckValue(v.Key, v.Value))
                .ToList(),
            DistinctValues = _values.Count,
            MoreValues = _moreValues,
            Findings = _findings.Values
                .OrderByDescending(f => f.Count)
                .Select(f => f.Build(limits, skipped))
                .ToList(),
            Unlisted = _unlisted,
        };

        private static ValueCheckCounts Counts(Dictionary<Standing, long> counts) => new()
        {
            Valid = counts.GetValueOrDefault(Standing.Valid),
            Invalid = counts.GetValueOrDefault(Standing.Invalid),
            Empty = counts.GetValueOrDefault(Standing.Empty),
            NotApplicable = counts.GetValueOrDefault(Standing.NotApplicable),
            Held = counts.GetValueOrDefault(Standing.Held),
        };
    }

    /// <summary>One finding's occurrences, the rows they are in, the values behind them and the example records kept.</summary>
    private sealed class FindingTally(string outcome, string at, string? rule, string message, string? onFail)
    {
        private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
        private readonly List<ValueCheckSample> _samples = [];
        private long _count;
        private long _rows;
        private long _lastRow = -1;
        private long _otherValues;

        public long Count => _count;

        public void Add(string? value, long row, ValueCheckSample record, ValueCheckRequest request, ValueCheckLimits limits)
        {
            if (_count >= request.SkipSamples && _samples.Count < request.Samples)
            {
                _samples.Add(record);
            }

            _count++;
            if (row != _lastRow)
            {
                _rows++;
                _lastRow = row;
            }

            if (value is null)
            {
                return;
            }

            if (_values.TryGetValue(value, out var seen))
            {
                _values[value] = seen + 1;
            }
            else if (_values.Count < limits.TrackedFindingValues)
            {
                _values[value] = 1;
            }
            else
            {
                _otherValues++;
            }
        }

        public ValueCheckFinding Build(ValueCheckLimits limits, long skipped)
        {
            var listed = _values
                .OrderByDescending(v => v.Value)
                .ThenBy(v => v.Key, StringComparer.Ordinal)
                .Take(limits.MaxFindingValues)
                .Select(v => new ValueCheckValue(v.Key, v.Value))
                .ToList();
            return new ValueCheckFinding
            {
                Outcome = outcome,
                At = at,
                Rule = rule,
                OnFail = onFail,
                Message = message,
                Count = _count,
                Rows = _rows,
                Values = listed,
                OtherValues = _otherValues + _values.Values.Sum() - listed.Sum(v => v.Count),
                Samples = _samples,
                SamplesFrom = Math.Min(skipped, _count),
            };
        }
    }
}
