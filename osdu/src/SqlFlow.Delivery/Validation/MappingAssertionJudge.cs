using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// A value an assertion with <c>onFail: omit</c> leaves out of a record once every assertion is judged: a property of an
/// object, or one value of a list.
/// </summary>
/// <param name="Holder">The object that holds the property, or the list that holds the value.</param>
/// <param name="Property">The property left out of <paramref name="Holder"/>, for an object; null for a value of a list.</param>
/// <param name="Value">The value left out of <paramref name="Holder"/>, for a list; null for a property.</param>
public readonly record struct Omission(JsonNode Holder, string? Property, JsonNode? Value);

/// <summary>
/// Judges the assertions a mapping states beside its properties (osdu/docs/reference/flow/mapping-assertions.md). The
/// record stage is judged on a whole record, so a document a render assembles and a record OSDU holds are judged by the
/// same code and come to the same answer; the incoming stage is judged on the value a row gives one node, as the render
/// reads it. Each condition is decided by <see cref="ValueComparer"/>, as an assertion flow's are.
/// </summary>
public static partial class MappingAssertionJudge
{
    /// <summary>
    /// Judges every assertion of <paramref name="assertions"/> on <paramref name="record"/> and returns the values the ones
    /// that failed with <c>onFail: omit</c> leave out, which <see cref="Omit"/> removes. Nothing in the record changes here,
    /// so every assertion judges the record as it was assembled, whatever another one leaves out.
    /// </summary>
    /// <param name="record">The record: a document as it will be sent, or a record OSDU holds.</param>
    /// <param name="assertions">The record-stage assertions judged, each with the node that states it.</param>
    /// <param name="log">Where what they find is collected.</param>
    public static IReadOnlyList<Omission> JudgeRecord(
        JsonObject record, IReadOnlyList<(MappingEntry Entry, NodeAssertion Assertion)> assertions, AssertionLog log)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(assertions);
        ArgumentNullException.ThrowIfNull(log);
        var omissions = new List<Omission>();
        foreach (var (entry, assertion) in assertions)
        {
            if (assertion.Stage != AssertionStage.Record)
            {
                continue;
            }

            try
            {
                JudgeOne(record, entry.Target, assertion, log, omissions);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A defect of the judge is never read as an assertion met: it fails under the assertion's action, and a value it
                // could not find is never left out on a guess.
                log.Judged();
                log.Fail(
                    At(entry.Target), At(entry.Target), assertion,
                    assertion.OnFail == AssertionAction.Omit ? AssertionAction.Report : assertion.OnFail,
                    string.Empty,
                    string.Create(CultureInfo.InvariantCulture, $"could not be judged ({ex.GetType().Name}: {ex.Message}), so it counts as failed{(assertion.OnFail == AssertionAction.Omit ? " and nothing was left out" : string.Empty)}"));
            }
        }

        return omissions;
    }

    /// <summary>
    /// What a mapping's assertions find of a record OSDU holds: its record-stage assertions judged on the record, which is
    /// the value they judge whether a render wrote it or OSDU stores it. The incoming ones judge the row a record was rendered
    /// from, which OSDU does not hold; a record of another entity type than the one the mapping renders is not judged at all,
    /// and null is answered for it. <paramref name="notes"/> says what was not judged, and why.
    /// </summary>
    public static AssertionFindings? OfStored(JsonObject record, MappingDefinition mapping, out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(mapping);
        var said = new List<string>();
        notes = said;
        var kind = record["kind"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;
        if (kind is null || !string.Equals(TypeOf(kind), TypeOf(mapping.Kind), StringComparison.Ordinal))
        {
            said.Add($"mapping {mapping.Reference} renders {mapping.Kind}, and the record is {(kind is null ? "of no kind" : kind)}, so its assertions were not judged");
            return null;
        }

        if (!string.Equals(kind, mapping.Kind, StringComparison.Ordinal))
        {
            said.Add($"mapping {mapping.Reference} renders {mapping.Kind}, and the record is {kind}; its assertions were judged on what the record holds at the same properties");
        }

        var assertions = mapping.Assertions().ToList();
        var incoming = assertions.Count(a => a.Assertion.Stage == AssertionStage.Incoming);
        if (incoming > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{incoming} assertion(s) of {mapping.Reference} judge the value a row gives before the mapping's modifiers, which OSDU does not hold, so they were not judged"));
        }

        if (mapping.Dataset.Assertions.Count > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{mapping.Dataset.Assertions.Count} rule(s) of {mapping.Reference} on the rows it renders from (dataset.assert) judge the ingestion tables, which OSDU does not hold, so they were not judged"));
        }

        var log = new AssertionLog(mapping.Reference);
        _ = JudgeRecord(record, assertions, log);
        return log.Findings();
    }

    /// <summary>The type a kind names, without its version: <c>authority:source:entityType</c>.</summary>
    private static string TypeOf(string kind)
    {
        var last = kind.LastIndexOf(':');
        return last < 0 ? kind : kind[..last];
    }

    /// <summary>
    /// Leaves out what <paramref name="omissions"/> name: a property is removed (a list written empty, since the mapping
    /// defines it), a value is removed from its list, and an object left with nothing is left out with it, as a render leaves
    /// out an object nothing fills; the record's <c>data</c> always stays.
    /// </summary>
    public static void Omit(JsonObject record, IReadOnlyList<Omission> omissions)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(omissions);
        foreach (var omission in omissions)
        {
            switch (omission.Holder)
            {
                case JsonArray list when omission.Value is not null:
                    list.Remove(omission.Value);
                    break;
                case JsonObject holder when omission.Property is not null && holder[omission.Property] is { } value:
                    if (value is JsonArray)
                    {
                        holder[omission.Property] = new JsonArray();
                    }
                    else
                    {
                        holder.Remove(omission.Property);
                        Prune(record, holder);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Judges a node's incoming assertions on the value the row gives it, before its modifiers. False when one that failed
    /// leaves the value out (<c>onFail: omit</c>), so the node gives no value.
    /// </summary>
    /// <param name="entry">The node.</param>
    /// <param name="incoming">The value the row gives it: the column, or what its expression computes.</param>
    /// <param name="column">Reads a column a condition names, of the row the node reads or of the dataset's own row.</param>
    /// <param name="path">Where the value came from, as a failure names it (<c>dataset.curves[3].curve_id</c>).</param>
    /// <param name="log">Where what the assertions find is collected.</param>
    public static bool JudgeIncoming(MappingEntry entry, object? incoming, Func<DatasetColumn, object?> column, string path, AssertionLog log)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(log);
        var keep = true;
        var at = At(entry.Target);
        foreach (var assertion in entry.IncomingAssertions)
        {
            try
            {
                if (!assertion.Where.All(filter => ValueComparer.Selects(filter.Condition, Present(Incoming(column(filter.Column!))))))
                {
                    continue;
                }

                var value = Incoming(incoming);
                if (Judge(assertion, value, log) is { } reason)
                {
                    log.Fail(at, path, assertion, assertion.OnFail, Shown(value), reason);
                    keep &= assertion.OnFail != AssertionAction.Omit;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Judged();
                log.Fail(
                    at, path, assertion, assertion.OnFail == AssertionAction.Omit ? AssertionAction.Report : assertion.OnFail, string.Empty,
                    string.Create(CultureInfo.InvariantCulture, $"could not be judged ({ex.GetType().Name}: {ex.Message}), so it counts as failed{(assertion.OnFail == AssertionAction.Omit ? " and nothing was left out" : string.Empty)}"));
            }
        }

        return keep;
    }

    /// <summary>
    /// A value a row holds as the comparer reads it: text as it is (blank text is no value), a number as a number, true or
    /// false, and a date as its RFC 3339 instant; null for no value.
    /// </summary>
    public static JsonNode? Incoming(object? raw) => raw switch
    {
        null or DBNull => null,
        string text => string.IsNullOrWhiteSpace(text) ? null : JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(raw, CultureInfo.InvariantCulture)),
        ulong whole => JsonValue.Create(whole),
        decimal number => JsonValue.Create(number),
        double number => double.IsFinite(number) ? JsonValue.Create(number) : null,
        float number => float.IsFinite(number) ? JsonValue.Create(NumberValues.Widen(number)) : null,
        _ => SourceRow.Stringify(raw) is { } text && !string.IsNullOrWhiteSpace(text) ? JsonValue.Create(text) : null,
    };

    /// <summary>
    /// A property path as findings name it: the template path without its <c>osdu.</c> (<c>data.Curves[].CurveID</c>,
    /// <c>tags['lra.dataID']</c>).
    /// </summary>
    public static string At(TemplatePath target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.RecordPath;
    }

    /// <summary>One assertion over the places a record holds its property: the record, or each item of the array it is in.</summary>
    private static void JudgeOne(JsonObject record, TemplatePath target, NodeAssertion assertion, AssertionLog log, List<Omission> omissions)
    {
        var at = At(target);
        if (assertion.AnyValue && target.IsRepeated)
        {
            JudgeAcrossItems(record, target, assertion, at, log);
            return;
        }

        foreach (var place in Places(record, target))
        {
            if (!assertion.Where.All(filter => ValueComparer.Selects(filter.Condition, Selected(record, place, target, filter.Field!))))
            {
                continue;
            }

            var value = place.Holder?[place.Name] is { } held && held.GetValueKind() != JsonValueKind.Null ? held : null;
            var op = assertion.Condition.Operator;
            if (value is JsonArray list && op is not (ValueOperator.Exists or ValueOperator.Empty or ValueOperator.Length or ValueOperator.Contains or ValueOperator.NotContains))
            {
                // A list's values are judged one by one; the operators that judge a list judge it whole.
                JudgeValues(assertion, list, place, at, log, omissions);
                continue;
            }

            if (Judge(assertion, value, log) is { } reason)
            {
                log.Fail(at, place.Path, assertion, assertion.OnFail, Shown(value), reason);
                if (assertion.OnFail == AssertionAction.Omit && value is not null && place.Holder is { } holder)
                {
                    omissions.Add(new Omission(holder, place.Name, null));
                }
            }
        }
    }

    /// <summary>
    /// A property inside the items of a repeated array asserted with <c>values: any</c>: the values of every item the
    /// <c>where</c> conditions select (each value of a list among them) are judged together, once, and hold when one meets
    /// the condition.
    /// </summary>
    private static void JudgeAcrossItems(JsonObject record, TemplatePath target, NodeAssertion assertion, string at, AssertionLog log)
    {
        var values = new List<JsonNode>();
        foreach (var place in Places(record, target))
        {
            if (!assertion.Where.All(filter => ValueComparer.Selects(filter.Condition, Selected(record, place, target, filter.Field!))))
            {
                continue;
            }

            switch (place.Holder?[place.Name])
            {
                case JsonArray list:
                    values.AddRange(list.Where(v => v is not null && v.GetValueKind() != JsonValueKind.Null)!);
                    break;
                case { } value when value.GetValueKind() != JsonValueKind.Null:
                    values.Add(value);
                    break;
            }
        }

        if (values.Count == 0)
        {
            return;
        }

        log.Judged();
        var reason = string.Empty;
        foreach (var value in values)
        {
            if (ValueComparer.Holds(assertion.Condition, value, out var why))
            {
                return;
            }

            reason = reason.Length == 0 ? why : reason;
        }

        log.Fail(
            at, at, assertion, assertion.OnFail, RecordValues.Describe(values),
            string.Create(CultureInfo.InvariantCulture, $"none of its {values.Count} values meets it; one {reason}"));
    }

    /// <summary>The values of a list, each judged on its own, or with <c>values: any</c> together.</summary>
    private static void JudgeValues(NodeAssertion assertion, JsonArray list, Place place, string at, AssertionLog log, List<Omission> omissions)
    {
        var values = list.Select((value, index) => (Value: value, Index: index)).Where(v => v.Value is not null && v.Value.GetValueKind() != JsonValueKind.Null).ToList();
        if (values.Count == 0)
        {
            return;
        }

        if (assertion.AnyValue)
        {
            log.Judged();
            var reason = string.Empty;
            foreach (var (value, _) in values)
            {
                if (ValueComparer.Holds(assertion.Condition, value!, out var why))
                {
                    return;
                }

                reason = reason.Length == 0 ? why : reason;
            }

            log.Fail(
                at, place.Path, assertion, assertion.OnFail, RecordValues.Describe(values.Select(v => v.Value!).ToList()),
                string.Create(CultureInfo.InvariantCulture, $"none of its {values.Count} values meets it; one {reason}"));
            return;
        }

        foreach (var (value, index) in values)
        {
            log.Judged();
            if (!ValueComparer.Holds(assertion.Condition, value!, out var why))
            {
                log.Fail(at, string.Create(CultureInfo.InvariantCulture, $"{place.Path}[{index}]"), assertion, assertion.OnFail, Shown(value), why);
                if (assertion.OnFail == AssertionAction.Omit)
                {
                    omissions.Add(new Omission(list, null, value));
                }
            }
        }
    }

    /// <summary>
    /// Whether one value meets an assertion: null when it does, or when there is nothing to judge (no value, for any
    /// condition but <c>exists</c> and <c>empty</c>); otherwise why it does not. A judgement made is counted.
    /// </summary>
    private static string? Judge(NodeAssertion assertion, JsonNode? value, AssertionLog log) => Judge(assertion.Condition, value, log);

    /// <summary>
    /// Whether <paramref name="value"/> meets <paramref name="condition"/>, as <see cref="Meets"/> says, the judgement made
    /// counted on <paramref name="log"/>: null when it meets it or is not judged, otherwise why it does not.
    /// </summary>
    internal static string? Judge(ValueCondition condition, JsonNode? value, AssertionLog log)
    {
        if (Meets(condition, value, out var reason) is not { } met)
        {
            return null;
        }

        log.Judged();
        return met ? null : reason;
    }

    /// <summary>
    /// Whether <paramref name="value"/> meets <paramref name="condition"/>: no value is judged only by <c>exists</c> (it fails
    /// <c>exists: true</c>) and <c>empty</c> (it meets <c>empty: true</c>), and passed over (null) by every other condition;
    /// a value is compared as the assertion flows compare it. <paramref name="reason"/> says why it does not meet it.
    /// </summary>
    internal static bool? Meets(ValueCondition condition, JsonNode? value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(condition);
        reason = null;
        switch (condition.Operator)
        {
            case ValueOperator.Exists:
                if ((value is not null) == condition.Flag)
                {
                    return true;
                }

                reason = value is null ? "has no value" : "has a value, and is expected to have none";
                return false;
            case ValueOperator.Empty when value is null:
                reason = condition.Flag ? null : "has no value, and is expected to hold one";
                return condition.Flag;
        }

        if (value is null)
        {
            return null;
        }

        return ValueComparer.Holds(condition, value, out reason);
    }

    /// <summary>
    /// What a <c>where</c> field reads for one place: inside the same repeated array as the property, the field of the same
    /// item; anywhere else (or with an explicit index), the field of the record.
    /// </summary>
    private static IReadOnlyList<JsonNode> Selected(JsonObject record, Place place, TemplatePath target, string field)
    {
        if (place.Item is { } item && target.Repeater is { } repeater
            && RecordPathNotation.TryParse(field, out var steps, out _) && !steps.Any(s => s.Kind == RecordPathStepKind.Index))
        {
            var dotted = RecordPathNotation.Write(steps.Where(s => s.Kind != RecordPathStepKind.Wildcard));
            if (RecordPathNotation.Below(dotted, repeater.SchemaPath) is { } inside && !inside.StartsWith("[]", StringComparison.Ordinal))
            {
                return RecordValues.Select(item, inside);
            }
        }

        return RecordValues.Select(record, field);
    }

    /// <summary>
    /// The places a record holds a property: for a property outside any repeated array, the object that holds it (null when
    /// an object on the way is not there); for one inside a repeated array, the object of each item that holds it.
    /// </summary>
    private static IEnumerable<Place> Places(JsonObject record, TemplatePath target)
    {
        var segments = target.Segments;
        var into = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].IntoArray)
            {
                into = i;
                break;
            }
        }

        if (into < 0)
        {
            var (holder, path) = Walk(record, segments, 0, segments.Count - 1, string.Empty);
            yield return new Place(holder, segments[^1].Name, Join(path, segments[^1].Name), null);
            yield break;
        }

        var (owner, ownerPath) = Walk(record, segments, 0, into, string.Empty);
        if (owner?[segments[into].Name] is not JsonArray items)
        {
            yield break;
        }

        var arrayPath = Join(ownerPath, segments[into].Name);
        for (var k = 0; k < items.Count; k++)
        {
            if (items[k] is not JsonObject item)
            {
                continue;
            }

            var itemPath = string.Create(CultureInfo.InvariantCulture, $"{arrayPath}[{k}]");
            var (holder, path) = Walk(item, segments, into + 1, segments.Count - 1, itemPath);
            yield return new Place(holder, segments[^1].Name, Join(path, segments[^1].Name), item);
        }
    }

    /// <summary>The object segments <paramref name="from"/> to <paramref name="to"/> lead to below <paramref name="start"/>, and its path; null when one is not there.</summary>
    private static (JsonObject? Holder, string Path) Walk(JsonObject start, IReadOnlyList<TemplatePathSegment> segments, int from, int to, string path)
    {
        JsonObject? current = start;
        for (var i = from; i < to && current is not null; i++)
        {
            path = Join(path, segments[i].Name);
            current = current[segments[i].Name] as JsonObject;
        }

        return (current, path);
    }

    /// <summary>Removes objects a removal left empty, up to the record's <c>data</c>: an item of a list, or a property.</summary>
    private static void Prune(JsonObject record, JsonObject emptied)
    {
        var current = emptied;
        while (current.Count == 0 && !ReferenceEquals(current, record) && !ReferenceEquals(current, record["data"]))
        {
            switch (current.Parent)
            {
                case JsonArray list:
                    list.Remove(current);
                    return;
                case JsonObject parent:
                    var name = parent.FirstOrDefault(p => ReferenceEquals(p.Value, current)).Key;
                    if (name is null)
                    {
                        return;
                    }

                    parent.Remove(name);
                    current = parent;
                    break;
                default:
                    return;
            }
        }
    }

    internal static IReadOnlyList<JsonNode> Present(JsonNode? value) => value is null ? [] : [value];

    internal static string Shown(JsonNode? value) => value is null ? "(no value)" : RecordValues.Text(value);

    private static string Join(string path, string name) => RecordPathNotation.Append(path, name);

    /// <summary>One place a record holds a property: the object holding it (null when it is not there), its name, its path, and the item it is in.</summary>
    private readonly record struct Place(JsonObject? Holder, string Name, string Path, JsonObject? Item);
}
