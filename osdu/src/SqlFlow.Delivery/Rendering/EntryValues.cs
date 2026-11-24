using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Expressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The value of one mapping entry for one row (osdu/docs/reference/flow/mapping-values.md): the condition, the source
/// or static value, the modifiers, the cache lookup, what an empty value does, and the conversion to the template
/// variable's type. A value that cannot be produced either leaves the variable out or holds the record with a reason
/// naming the target.
/// </summary>
internal static partial class EntryValues
{
    /// <summary>The value of <paramref name="entry"/> for the row, converted to its variable's type, or null when the variable is left out.</summary>
    public static JsonNode? Evaluate(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
        => Evaluate(entry, root, item, renderer, holds, usages, searched, out _);

    /// <summary>
    /// The value of <paramref name="entry"/> for the row, as <see cref="Evaluate(MappingEntry, SourceRow, SourceRow?, MappingRenderer, List{string}, List{CacheUsage}, RenderTrail)"/>
    /// gives it, with <paramref name="applied"/> false when the entry's <c>$when</c> does not hold for the row, which is
    /// the one reason besides an empty value that a variable is left out.
    /// </summary>
    public static JsonNode? Evaluate(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out bool applied)
    {
        if (entry.RestrictWhenMissing)
        {
            return Restricting(entry, root, item, renderer, holds, usages, searched, out applied);
        }

        var path = entry.Target.Text;
        applied = true;
        if (entry.AppliesWhen is { } condition && !Applies(condition, root, item, renderer, holds, path))
        {
            applied = false;
            return null;
        }

        if (entry.IsCoalesce)
        {
            return Coalesced(entry, root, item, renderer, holds, usages, searched);
        }

        if (entry.IsList)
        {
            return Listed(entry, root, item, renderer, holds, usages, searched);
        }

        if (entry.IsObject)
        {
            return Assembled(entry, root, item, renderer, holds, usages, searched);
        }

        object? raw;
        if (entry.Static is { } fixedValue)
        {
            raw = Expand(fixedValue, renderer);
        }
        else if (entry.Source!.ReadsRow)
        {
            object? read;
            if (entry.Source.Expression is { } expression)
            {
                if (!expression.TryEvaluate(Input(root, item, renderer), out read, out var problem))
                {
                    holds.Add($"{path}: {problem}");
                    return null;
                }
            }
            else
            {
                read = Read(entry.Source.Column!, root, item);
            }

            // What the row gives is judged before the modifiers change it; one that fails with onFail: omit leaves the value
            // out whatever $required says, since the mapping asked for that.
            if (entry.Assertions.Count > 0 && searched.Assertions is { } log
                && !MappingAssertionJudge.JudgeIncoming(entry, read, column => Read(column, root, item), IncomingPath(entry, searched.Item), log))
            {
                return null;
            }

            if (!TryModify(entry, read, root, item, renderer, holds, usages, searched, out raw))
            {
                return null;
            }

            if (IsEmpty(raw))
            {
                // Said only where something reads it: an optional value left out costs a delivery's render nothing.
                if (entry.Required || searched.NotesMisses)
                {
                    var empty = $"{path}: {EmptyRead(entry, read)}";

                    // A value the row held that the modifiers turned into nothing is no empty source value: what it held is the cause.
                    if (IsEmpty(read))
                    {
                        var columns = entry.Source.Expression is { } computed ? computed.Columns.ToList() : [entry.Source.Column!];
                        searched.Miss(new RenderMiss { Kind = RenderMissKind.Empty, Reason = empty, Columns = columns });
                    }

                    if (entry.Required)
                    {
                        holds.Add($"{empty}, and the entry is required");
                    }
                }

                return null;
            }
        }
        else if (entry.Source!.Kind == MappingSourceKind.Search)
        {
            raw = Searched(entry, root, item, renderer, holds, usages, searched);
            if (raw is null)
            {
                return null;
            }
        }
        else if (entry.Source.Kind == MappingSourceKind.Parent)
        {
            raw = FromParent(entry, root, item, renderer, holds, usages, searched);
            if (raw is null)
            {
                return null;
            }
        }
        else if (entry.Source.Kind == MappingSourceKind.Child)
        {
            raw = ToChild(entry, root, renderer, holds, searched);
            if (raw is null)
            {
                return null;
            }
        }
        else if (entry.Source.Kind == MappingSourceKind.Base)
        {
            raw = FromBase(entry, root, item, renderer, holds, usages, searched);
            if (raw is null)
            {
                return null;
            }
        }
        else if (entry.Source.Kind == MappingSourceKind.Link)
        {
            raw = Linked(entry, renderer, holds, searched);
            if (raw is null)
            {
                return null;
            }
        }
        else
        {
            raw = entry.FindAll is not null
                ? CachedAll(entry, root, item, renderer, holds, usages, searched)
                : Cached(entry, root, item, renderer, holds, usages, searched);
            if (raw is null)
            {
                return null;
            }
        }

        return raw is null ? null : Convert(raw, renderer.Schema.Resolve(entry.Target.SchemaPath), path, holds);
    }

    /// <summary>The clause a required node's hold ends its reason with, or puts before the reasons of a <c>$coalesce</c>'s alternatives.</summary>
    private const string RequiredClause = ", and the entry is required";

    /// <summary>
    /// Why an optional entry that wrote nothing for the row gave no value: the entry is asked again as a required one, whose
    /// render states why it would hold the record (an empty column, no record in the cache, a search that found nothing,
    /// every alternative of a <c>$coalesce</c> and why each gave nothing), and that is said without the target and the
    /// required clause. What the second evaluation reads is what the first already read, so it asks the platform nothing
    /// new and records nothing.
    /// </summary>
    /// <param name="entry">The entry that wrote nothing.</param>
    /// <param name="root">The dataset's own row.</param>
    /// <param name="item">The child row of the item the entry is in, or null for the record's own entries.</param>
    /// <param name="renderer">The renderer the entry belongs to.</param>
    /// <param name="baseRecord">For a fragment, the record it adds to, which its <c>$base</c> nodes read; null for a whole record.</param>
    public static string WhyEmpty(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, JsonObject? baseRecord = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(renderer);
        var said = new List<string>();
        var log = entry.IncomingAssertions.Any() ? new AssertionLog(renderer.Mapping.Reference) : null;
        var value = Evaluate(entry with { Required = true, RestrictWhenMissing = false, AppliesWhen = null }, root, item, renderer, said, [], new RenderTrail { Assertions = log, Base = baseRecord }, out _);
        if (said.Count > 0)
        {
            return Reason(said[0], entry.Target.Text);
        }

        if (value is null && log?.Findings().Failures.FirstOrDefault(f => f.OnFail == AssertionWords.Omit) is { } omitted)
        {
            return $"its assertion \"{omitted.Assertion}\" leaves it out: the row's value {omitted.Message}";
        }

        return entry.IsList && value is null ? $"none of the {entry.Parts.Count} items of the list gives a value" : "gives no value";
    }

    /// <summary>What an alternative said about why it gave nothing, without the target it names and the required clause it adds.</summary>
    private static string Reason(string said, string path)
    {
        var reason = said.StartsWith(path + ": ", StringComparison.Ordinal) ? said[(path.Length + 2)..] : said;
        return reason.EndsWith(RequiredClause, StringComparison.Ordinal) ? reason[..^RequiredClause.Length] : reason;
    }

    /// <summary>
    /// Whether a <c>$when</c> or a <c>$where</c> holds for the row. A condition the row gives a value it cannot test
    /// (text where a number is compared, say) does not hold, and holds the record with the reason: whether the property
    /// belongs in the record is then unknown, and a record written without it would hide that.
    /// </summary>
    public static bool Applies(MappingExpression condition, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, string path)
    {
        if (condition.TryTest(Input(root, item, renderer), out var applies, out var problem))
        {
            return applies;
        }

        holds.Add($"{path}: {problem}");
        return false;
    }

    /// <summary>What an expression reads for a row: the row's columns, the dataset's own row's, and the render's parameters.</summary>
    private static ExpressionInput Input(SourceRow root, SourceRow? item, MappingRenderer renderer)
        => new(column => Read(column, root, item), renderer.ParameterValue);

    private static object? Read(DatasetColumn column, SourceRow root, SourceRow? item)
        => column.Child is null ? root.Get(column.Column) : item?.Get(column.Column);

    private static bool IsEmpty(object? value) => value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    /// <summary>
    /// Where the value an incoming assertion judges came from: the column of the dataset's own row (<c>dataset.log_name</c>)
    /// or of a child row (<c>dataset.curves[3].curve_id</c>), or the row an expression read and the expression.
    /// </summary>
    private static string IncomingPath(MappingEntry entry, int? item)
    {
        var source = entry.Source!;
        string Row(string? child) => child is null
            ? DatasetColumn.Prefix
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DatasetColumn.Prefix}.{child}[{item ?? 0}]");
        if (source.Column is { } column)
        {
            return $"{Row(column.Child)}.{column.Column}";
        }

        var child = source.Expression!.Columns.Select(c => c.Child).FirstOrDefault(c => c is not null);
        return $"{Row(child)}: {source.Expression.Text}";
    }

    /// <summary>
    /// Why a value read from the row came to nothing: the column (or the expression) gave nothing, or it gave a value its
    /// modifiers turned into nothing (a replace to <c>~</c>, a split with too few parts), which says the value it held.
    /// </summary>
    private static string EmptyRead(MappingEntry entry, object? read)
    {
        var source = entry.Source!;
        if (IsEmpty(read))
        {
            return source.Expression is { } computed ? $"{computed} gives no value" : $"{source.Column} is empty";
        }

        var held = source.Expression is { } expression ? $"{expression} gives" : $"{source.Column} is";
        return $"{held} '{Clip(SourceRow.Stringify(read) ?? string.Empty)}', which its modifiers ({ModifierText(entry.Modifiers)}) turn into no value";
    }

    /// <summary>A cached value as a hold reason quotes it: whole when short, its start otherwise.</summary>
    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "...";

    /// <summary>
    /// A value the cache or the platform does not have: noted on the trail, and then a required entry holds the record and an
    /// optional one leaves the variable out.
    /// </summary>
    private static object? Missing(MappingEntry entry, RenderMiss miss, List<string> holds, RenderTrail searched)
    {
        searched.Miss(miss);
        if (entry.Required)
        {
            holds.Add(miss.Reason);
        }

        return null;
    }

    private static string WithVersionSeparator(string id) => id.EndsWith(':') ? id : id + ":";

    /// <summary>
    /// An OSDU id a value already was, as a relationship takes it. One naming a version, as OSDU's reference pattern reads
    /// an id whose code holds no colon and is followed by the version's digits
    /// (<c>dev:master-data--Wellbore:1234:1700000000000</c>), is written as it is: the separator added after it would make
    /// the version part of the code, a reference to another record. Any other is written with the separator a reference to
    /// the latest version ends in, a code holding colons of its own included
    /// (<c>dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031:</c>).
    /// </summary>
    internal static string AsReference(string id)
    {
        var trimmed = id.Trim();
        return !trimmed.EndsWith(':') && CachedReferences.Parse(trimmed) is { } named && !string.Equals(named.Id, trimmed, StringComparison.Ordinal)
            ? trimmed
            : WithVersionSeparator(trimmed);
    }

    private static JsonNode Expand(JsonNode node, MappingRenderer renderer) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(MappingEntry.ExpandParameters(text, renderer.ParameterValue)),
        JsonObject obj => new JsonObject(obj.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value is null ? null : Expand(kv.Value, renderer)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Expand(item, renderer)).ToArray()),
        _ => node.DeepClone(),
    };

    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+--[\w\-\.]+:[\w\-\.\:\%]+$")]
    private static partial Regex OsduId();
}
