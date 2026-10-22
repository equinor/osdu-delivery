using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Expressions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
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

            if (!TryModify(entry, read, root, item, renderer, holds, usages, out raw))
            {
                return null;
            }

            if (IsEmpty(raw))
            {
                if (entry.Required)
                {
                    holds.Add($"{path}: {EmptyRead(entry, read)}, and the entry is required");
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
        else
        {
            raw = entry.FindAll is not null
                ? CachedAll(entry, root, item, renderer, holds, usages)
                : Cached(entry, root, item, renderer, holds, usages);
            if (raw is null)
            {
                return null;
            }
        }

        return raw is null ? null : Convert(raw, renderer.Schema.Resolve(entry.Target.SchemaPath), path, holds);
    }

    /// <summary>
    /// The value of a <c>$coalesce</c> node: the first of its alternatives that gives one, each tried as an optional node
    /// of its own. An alternative that gives nothing (an empty column, no record in the cache, a key its table does not
    /// list, an id the partition holds no record under) passes to the next. One that meets a mistake (a date that is not a
    /// date, a value several records answer to) holds the record as it would on its own, since what it would have given
    /// is unknown, not absent. One still waiting for a search stops the node there, and the render finishes once the
    /// answer is in, so a later alternative never stands in for an earlier one that has not been asked yet. What every
    /// alternative tried read from the cache is recorded, so a later version that would let an earlier one give a value
    /// reaches the record. When none gives one, the node's required flag decides, naming why each gave nothing.
    /// </summary>
    private static JsonNode? Coalesced(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var alternatives = Alternatives(entry);
        for (var i = 0; i < alternatives.Count; i++)
        {
            var mistakes = new List<string>();
            var waiting = searched.Unanswered.Count;
            var read = usages.Count;
            var value = Evaluate(alternatives[i], root, item, renderer, mistakes, usages, searched);
            if (mistakes.Count > 0)
            {
                holds.AddRange(mistakes);
                return null;
            }

            if (searched.Unanswered.Count > waiting)
            {
                return null;
            }

            if (value is not null)
            {
                var unverified = usages.Skip(read).Any(u => u.Kind == CacheUsageKind.Unverified);
                // The origin names what the alternative reads; whether the id it wrote was unverified is the choice's own flag.
                searched.Choose(entry.Target.Text, i + 1, alternatives.Count, Origin(alternatives[i] with { Unverified = false }), unverified);
                return value;
            }
        }

        if (entry.Required)
        {
            var path = entry.Target.Text;
            var reasons = new List<string>(alternatives.Count);
            for (var i = 0; i < alternatives.Count; i++)
            {
                // Asked again as a required node, each alternative says why it gave nothing; what it reads is already known.
                var said = new List<string>();
                Evaluate(alternatives[i] with { Required = true }, root, item, renderer, said, [], new RenderTrail());
                reasons.Add($"{i + 1}. {(said.Count > 0 ? Reason(said[0], path) : "gives no value")}");
            }

            holds.Add($"{path}: none of the {alternatives.Count} alternatives of $coalesce gives a value, and the entry is required: {string.Join("; ", reasons)}");
        }

        return null;
    }

    /// <summary>
    /// A <c>$coalesce</c> node's alternatives as nodes of their own, in order: the entry itself without the node's settings,
    /// then the rest. Each is optional, so an alternative that gives nothing passes to the next.
    /// </summary>
    internal static IReadOnlyList<MappingEntry> Alternatives(MappingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return [entry with { Alternatives = [], AppliesWhen = null, Required = false, Location = null, Assertions = [] }, .. entry.Alternatives];
    }

    /// <summary>
    /// Why an optional entry that wrote nothing for the row gave no value: the entry is asked again as a required one, whose
    /// render states why it would hold the record (an empty column, no record in the cache, a search that found nothing,
    /// every alternative of a <c>$coalesce</c> and why each gave nothing), and that is said without the target and the
    /// required clause. What the second evaluation reads is what the first already read, so it asks the platform nothing
    /// new and records nothing.
    /// </summary>
    public static string WhyEmpty(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(renderer);
        var said = new List<string>();
        var log = entry.IncomingAssertions.Any() ? new AssertionLog(renderer.Mapping.Reference) : null;
        var value = Evaluate(entry with { Required = true, AppliesWhen = null }, root, item, renderer, said, [], new RenderTrail { Assertions = log }, out _);
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
        const string Required = ", and the entry is required";
        return reason.EndsWith(Required, StringComparison.Ordinal) ? reason[..^Required.Length] : reason;
    }

    /// <summary>
    /// The value of <paramref name="entry"/> in a record's shape, without a row: a static value converted exactly as a
    /// render converts it, or a placeholder naming the type the template gives the variable and where the value comes from
    /// (a list of one where the variable is a list of values). A value that could not be written whatever the row, a single
    /// value on an object, is noted and left out, as a render holds it.
    /// </summary>
    public static JsonNode? Describe(MappingEntry entry, MappingRenderer renderer, List<string> notes)
    {
        var path = entry.Target.Text;
        var property = renderer.Schema.Resolve(entry.Target.SchemaPath);
        if (entry.IsList)
        {
            // Each item as it is drawn on its own, one after another: a literal as it renders, a node as its placeholder.
            var items = new JsonArray();
            foreach (var part in entry.Parts)
            {
                var drawn = Describe(part, renderer, notes);
                foreach (var value in drawn is JsonArray set ? set.ToList() : [drawn])
                {
                    if (value is not null)
                    {
                        items.Add(value.DeepClone());
                    }
                }
            }

            return items.Count > 0 ? items : null;
        }

        if (entry.IsObject)
        {
            // An item of a list of objects as its properties are drawn, each at its place in the item.
            var drawn = new JsonObject();
            foreach (var itemProperty in entry.Properties)
            {
                if (Describe(itemProperty, renderer, notes) is { } value)
                {
                    MappingRenderer.SetPath(drawn, itemProperty.Target.WithinItem, value);
                }
            }

            return drawn.Count > 0 ? drawn : null;
        }

        if (entry.Static is { } fixedValue)
        {
            return Convert(Expand(fixedValue, renderer), property, path, notes);
        }

        var type = property?.Type ?? SchemaType.Any;
        switch (type)
        {
            case SchemaType.String:
                return JsonValue.Create(Placeholder(entry, property!.Format is { } format ? $"{format} string" : Name(type)));
            case SchemaType.Number:
            case SchemaType.Integer:
            case SchemaType.Boolean:
                return JsonValue.Create(Placeholder(entry, Name(type)));
            case SchemaType.Any:
                return JsonValue.Create(Placeholder(entry, "value"));
        }

        if (type == SchemaType.Array && property!.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var itemFormat = property?.ItemFormat;
            return new JsonArray(JsonValue.Create(Placeholder(entry, itemFormat is not null ? $"{itemFormat} string" : Name(itemType))));
        }

        // An object, or a list of objects: a cached field or a section of the record's parent can carry one whole, and a
        // dataset column never can.
        if (entry.ValueNodes.Any(node => node.Source?.Kind is MappingSourceKind.Cache or MappingSourceKind.Parent))
        {
            return JsonValue.Create(Placeholder(entry, Name(type)));
        }

        notes.Add($"{path}: a single value from {entry.Source} cannot be written where the template takes an {Name(type)}");
        return null;
    }

    /// <summary>
    /// A placeholder for a value read from a row or the cache: <c>&lt;number from dataset.depth | trim, optional&gt;</c>, and
    /// for a <c>$coalesce</c> node its alternatives in order: <c>&lt;string from dataset.log_name, else dataset.log_source&gt;</c>.
    /// </summary>
    private static string Placeholder(MappingEntry entry, string type)
    {
        var origin = string.Join(", else ", entry.ValueNodes.Select(Origin));
        var optional = entry.Required ? string.Empty : ", optional";
        var when = entry.AppliesWhen is { } condition ? $", when {condition}" : string.Empty;
        return $"<{type} from {origin}{optional}{when}>";
    }

    /// <summary>Where one value node reads its value, as a placeholder names it; a literal alternative is its text.</summary>
    private static string Origin(MappingEntry node)
    {
        if (node.Static is { } literal)
        {
            return literal.ToJsonString();
        }

        var source = node.Source!;
        if (node.FindAll is { } findAll)
        {
            return $"{source} of every {findAll.Type} row where {FindAllText(node, findAll)}";
        }

        var origin = source.Resolves
            ? node.FindBy.Count == 0 ? source.ToString() : $"{source} by {FindText(node)}"
            : node.Modifiers.Count == 0 ? source.ToString() : $"{source} | {ModifierText(node.Modifiers)}";
        return node.Unverified ? origin + " (unverified)" : origin;
    }

    /// <summary>A <c>$findAll</c>'s lines as one phrase, its column operand with the modifiers that change it.</summary>
    private static string FindAllText(MappingEntry node, FindAllQuery findAll)
    {
        var operand = findAll.Operand.Column is { } column && node.Modifiers.Count > 0
            ? $"({column} | {ModifierText(node.Modifiers)})"
            : findAll.Operand.ToString();
        return string.Join(" and ", findAll.Empty.Select(field => $"{ReferenceField.Normalize(field)} is empty")
            .Prepend($"{ReferenceField.Normalize(findAll.Field)} = {operand}"));
    }

    /// <summary>
    /// A cache source's findBy lines as one phrase, consecutive lines comparing the same value run together
    /// (<c>Code/Name = (dataset.unit | trim)</c>); the modifiers change the value compared, so they sit with it.
    /// </summary>
    private static string FindText(MappingEntry entry)
    {
        // A cached field is stored without its data. root, so either spelling names it; a search compares the path
        // exactly as it is written, so it is shown as written.
        var searched = entry.Source?.Kind == MappingSourceKind.Search;
        var groups = new List<(List<string> Fields, string Operand)>();
        foreach (var find in entry.FindBy)
        {
            var field = searched ? find.Field : ReferenceField.Normalize(find.Field);
            var operand = find.Literal is not null
                ? $"'{find.Literal}'"
                : entry.Modifiers.Count == 0 ? find.Column!.ToString() : $"({find.Column} | {ModifierText(entry.Modifiers)})";
            if (groups.Count > 0 && string.Equals(groups[^1].Operand, operand, StringComparison.Ordinal))
            {
                groups[^1].Fields.Add(field);
            }
            else
            {
                groups.Add(([field], operand));
            }
        }

        return string.Join(" or ", groups.Select(g => $"{string.Join('/', g.Fields)} = {g.Operand}"));
    }

    /// <summary>The modifiers as a pipeline; a replace of more than a few values is counted rather than listed.</summary>
    private static string ModifierText(IReadOnlyList<Modifier> modifiers) => string.Join(" | ", modifiers.Select(modifier => modifier.Kind switch
    {
        ModifierKind.Split => $"split('{modifier.Separator}', {modifier.Part})",
        ModifierKind.Replace when modifier.Replacements.Count > 3 => $"replace({modifier.Replacements.Count} values"
            + (modifier.Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $"; otherwise {modifier.Otherwise}") + ")",
        _ => modifier.ToString(),
    }));

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
    /// The value of a <c>$parent</c> node (osdu/docs/reference/flow/mapping-parent.md): the section at its path of the record's
    /// parent, the record of the kind the template is linked to whose OSDU id is the record's own with the parent's entity
    /// type. The parent is read from the platform between two renders, as a search is asked: a render that meets it first
    /// stops there, naming it, and the caller reads it and renders again. A parent the platform does not hold holds the
    /// record, whatever <c>$required</c> says: what the record takes from it is unknown, not absent. A parent without the
    /// section is an empty value, which <c>$required</c> decides. The section is copied whole, an object or a list as the
    /// parent holds it, and a single value passes through the node's modifiers like a column's.
    /// </summary>
    private static object? FromParent(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var section = entry.Source!.ParentPath!;
        if (renderer.ParentOf(root) is not { } question)
        {
            // A row without a complete key is held for that already; a template naming no parent is refused by the preflight,
            // and said here too, for a render that reaches this without one.
            if (renderer.Schema.Parent is null)
            {
                holds.Add(
                    $"{path}: $parent reads the record's parent, and template {renderer.Mapping.Template} is linked to none ({Templates.TemplateParent.Key}{(renderer.Schema.ParentProblem is { } problem ? ": " + problem : string.Empty)})");
            }

            return null;
        }

        if (!renderer.Search.TryRead(question, out var answer))
        {
            // Not read yet. The render stops here rather than guess, and the caller reads the parent and renders again.
            searched.Ask(question);
            return null;
        }

        searched.Read(question, answer);
        if (!answer.Found)
        {
            holds.Add(
                $"{path}: the record's parent {question.Id} ({question.Kind}) is not in OSDU, or the flow's identity may not read it, and the record takes {section} from it");
            return null;
        }

        var value = answer.At(section);
        if (value is null || (value is JsonValue text && text.TryGetValue<string>(out var said) && string.IsNullOrWhiteSpace(said)))
        {
            if (entry.Required)
            {
                holds.Add($"{path}: the record's parent {question.Id} holds no {section}, and the entry is required");
            }

            return null;
        }

        // The answer is shared by every render that reads the parent, so what is written is a copy of it.
        var copy = value.DeepClone();
        if (entry.Modifiers.Count == 0)
        {
            return copy;
        }

        if (copy is JsonObject or JsonArray)
        {
            holds.Add($"{path}: $modifiers change a single value, and the parent's {section} is {(copy is JsonArray ? "a list" : "an object")}");
            return null;
        }

        if (!TryModify(entry, Native(copy), root, item, renderer, holds, usages, out var modified))
        {
            return null;
        }

        if (IsEmpty(modified))
        {
            if (entry.Required)
            {
                holds.Add($"{path}: the parent's {section} gives no value once modified, and the entry is required");
            }

            return null;
        }

        return modified;
    }

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

    /// <summary>
    /// Applies the modifiers in order. False when a modifier could not read the value (a date that is not a date), which
    /// holds the record whatever the entry's required flag says: the value was there, and it was wrong. A replace reading a
    /// cached table records the rows it read among the render's cache usages.
    /// </summary>
    private static bool TryModify(
        MappingEntry entry, object? value, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out object? result)
    {
        var path = entry.Target.Text;
        result = value;
        foreach (var modifier in entry.Modifiers)
        {
            if (result is null)
            {
                return true;
            }

            var text = result is string s ? s : SourceRow.Stringify(result);
            switch (modifier.Kind)
            {
                case ModifierKind.Trim:
                    result = text?.Trim();
                    break;
                case ModifierKind.Upper:
                    result = text?.ToUpperInvariant();
                    break;
                case ModifierKind.Lower:
                    result = text?.ToLowerInvariant();
                    break;
                case ModifierKind.Split:
                    result = Split(text, modifier.Separator!, modifier.Part!.Value);
                    break;
                case ModifierKind.Replace:
                    if (!TryReplace(modifier, text, path, renderer, holds, usages, out result))
                    {
                        return false;
                    }

                    break;
                case ModifierKind.Equals:
                    result = text is null ? null : string.Equals(text.Trim(), modifier.Text?.Trim(), StringComparison.OrdinalIgnoreCase);
                    break;
                case ModifierKind.Date:
                    if (!DateValues.TryRead(result, modifier.Text, out var date))
                    {
                        holds.Add(modifier.Text is null
                            ? $"{path}: '{text}' is not an ISO 8601 date or date-time, such as 2026-09-01 or 2026-09-01T10:15:30Z; give the format it is written in, such as date: dd.MM.yyyy"
                            : $"{path}: '{text}' is not a date/time in the format {modifier.Text}");
                        result = null;
                        return false;
                    }

                    result = date;
                    break;
                case ModifierKind.Number:
                    if (!NumberValues.TryRead(result, modifier.DecimalSeparator, modifier.GroupSeparator, out var number, out var numberProblem))
                    {
                        holds.Add($"{path}: {numberProblem}");
                        result = null;
                        return false;
                    }

                    result = number;
                    break;
                case ModifierKind.Id:
                    if (!TryBuildId(modifier.Id!, text, entry, root, item, renderer, holds, usages, out result))
                    {
                        return false;
                    }

                    break;
                case ModifierKind.Ref:
                    if (renderer.ReferenceTemplate(entry, out var referenceProblem) is not { } reference)
                    {
                        holds.Add($"{path}: {referenceProblem}");
                        result = null;
                        return false;
                    }

                    if (!TryBuildId(reference, text, entry, root, item, renderer, holds, usages, out result))
                    {
                        return false;
                    }

                    break;
                default:
                    throw new DeliveryException($"{path}: modifier '{modifier.Kind}' is not supported.");
            }
        }

        return true;
    }

    private static string? Split(string? text, string separator, int part)
    {
        if (text is null)
        {
            return null;
        }

        var parts = separator == " " ? Whitespace().Split(text.Trim()) : text.Split(separator, StringSplitOptions.None);
        if (part > parts.Length)
        {
            return null;
        }

        var value = parts[part - 1].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Replaces the trimmed value by what the table lists for it (<see cref="ReplaceTables.Find"/>): an exact key first, then
    /// the one key that matches once case is ignored. A listed value may become no value. An unlisted value becomes what
    /// <c>otherwise</c> says, by default itself; an empty value stays empty, since there is nothing to look up. False, with a
    /// hold, when several keys match only once case is ignored and give different values: taking one would write a value
    /// nobody chose. A table read from the cache is looked up the same way (<see cref="TryReplaceFromCache"/>).
    /// </summary>
    private static bool TryReplace(
        Modifier modifier, string? text, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out object? result)
    {
        result = null;
        if (text is null)
        {
            return true;
        }

        var value = text.Trim();
        if (value.Length == 0)
        {
            result = value;
            return true;
        }

        if (modifier.Table is { } cached)
        {
            return TryReplaceFromCache(modifier, cached, value, path, renderer, holds, usages, out result);
        }

        var lookup = ReplaceTables.Find(ReplaceTables.Of(modifier.Replacements), ReplaceTables.KeyField, ReplaceTables.ValueField, value);
        switch (lookup.Kind)
        {
            case ReplaceLookupKind.Found:
                result = lookup.Value?.Text;
                return true;
            case ReplaceLookupKind.Ambiguous:
                var keys = string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"));
                holds.Add($"{path}: '{value}' matches the replace keys {keys} only once case is ignored, and they replace it with different values; make the incoming value exact");
                return false;
            default:
                result = Otherwise(modifier.Otherwise, value);
                return true;
        }
    }

    /// <summary>
    /// Replaces the value by what a table read from the cache gives for it: the row whose <c>match</c> field holds the
    /// value, by the cache's rules, gives what it holds at <c>field</c> (<see cref="ReplaceTables.Fields"/> settles either
    /// when the replace leaves it out). A row whose field is empty gives no value; <c>otherwise</c> decides only when no
    /// row holds the value.
    /// </summary>
    /// <remarks>
    /// Every row the replacement was decided by is recorded among the render's cache usages: the value it matched, and what
    /// it gave or that it gave nothing. A key a lookup table lists no row under is recorded too. So a later version of the
    /// table that changes a row, empties it, drops it, or comes to list a key it did not, tags exactly the records built
    /// from it. The record is held, whatever the entry's required flag says, when the cache version holds no such table,
    /// when its fields cannot be settled, when several rows answer to the value with different values, or when the row
    /// holds several values at its field: each of those leaves a value nobody chose.
    /// </remarks>
    private static bool TryReplaceFromCache(
        Modifier modifier, CachedReplaceTable table, string value, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out object? result)
    {
        result = null;
        var version = CacheLabel(renderer.Context);
        var type = renderer.References.Type(table.CacheType);
        if (type is null)
        {
            holds.Add($"{path}: replace reads $cache.{table.CacheType}, and {version} holds no type '{table.CacheType}'");
            return false;
        }

        if (ReplaceTables.Fields(table, type, out var problem) is not { } fields)
        {
            holds.Add($"{path}: {problem}");
            return false;
        }

        var lookup = ReplaceTables.Find(type, fields.Match, fields.Field, value);
        if (lookup.Kind == ReplaceLookupKind.Ambiguous)
        {
            var rows = string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"));
            holds.Add(
                $"{path}: '{value}' matches the {type.Name} rows {rows} by {fields.Match} only once case is ignored in {version}, and they replace it with different values; make the incoming value exact");
            return false;
        }

        if (lookup.Kind == ReplaceLookupKind.NotListed)
        {
            // Only a key can be told apart from every other value a row might come to hold, so it is a key a lookup table
            // does not list that is recorded; a key longer than any the table can hold never will be listed.
            if (type.IsLookup && fields.Match.Equals(type.Key, StringComparison.OrdinalIgnoreCase) && value.Length <= LookupKeys.MaxLength)
            {
                usages.Add(new CacheUsage(type.Name, CacheUsage.ListingKey(value), fields.Field, value, CacheUsageKind.Unlisted));
            }

            result = Otherwise(modifier.Otherwise, value);
            return true;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, fields.Match, value, CacheUsageKind.Match));
        }

        if (lookup.Value is not { } given || given.Node is JsonArray { Count: 0 })
        {
            foreach (var row in lookup.Rows)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, fields.Field, string.Empty, CacheUsageKind.Empty));
            }

            return true;
        }

        if (ReplaceTables.SingleText(given) is not { } replaced)
        {
            var held = given.Node is JsonArray many ? $"{many.Count} values" : "an object";
            holds.Add(
                $"{path}: {type.Name} '{lookup.Rows[0].Id}' holds {held} at '{fields.Field}' in {version} ({Clip(given.Text)}), and a replace turns '{value}' into one value; replace by a field that holds one");
            return false;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, fields.Field, given.Text, CacheUsageKind.Value));
        }

        result = replaced;
        return true;
    }

    /// <summary>A cached value as a hold reason quotes it: whole when short, its start otherwise.</summary>
    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "...";

    /// <summary>What an unlisted value becomes under a replace's <c>otherwise</c>.</summary>
    private static string? Otherwise(ReplaceFallback fallback, string value) => fallback.Kind switch
    {
        ReplaceFallbackKind.Keep => value,
        ReplaceFallbackKind.Empty => null,
        _ => fallback.Text,
    };

    /// <summary>
    /// Resolves a search source: each findBy line in turn asks the platform for the one record whose property is exactly
    /// the line's value, and the first line that finds exactly one wins. A line the run has not asked yet stops the render
    /// there, naming the question, because what the later lines are asked depends on what this one finds. A value that is
    /// already an OSDU id of the kind searched names its record without a search.
    /// </summary>
    /// <remarks>
    /// Doubt holds the record whatever the entry's required flag says: a value several records answer to, a query the
    /// platform refused, or a value that could not be asked for on a line when no other line found the record. Each of
    /// those says nothing about which record was meant, and delivering the record without the reference, or with one
    /// picked from several, would put a wrong document into OSDU with nothing to show it was a guess. Only a value every
    /// line asked for and found nothing for is a clean miss, which an optional entry leaves out as a cache miss does.
    /// </remarks>
    private static object? Searched(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var name = entry.Source!.CacheType!;
        var outcomes = new List<string>();
        var unaskable = false;
        foreach (var find in entry.FindBy)
        {
            string? value;
            if (find.Literal is not null)
            {
                value = find.Literal;
            }
            else
            {
                if (!TryModify(entry, Read(find.Column!, root, item), root, item, renderer, holds, usages, out var modified))
                {
                    return null;
                }

                value = modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified);
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!renderer.Searches.TryField(name, find.Field, out var search, out var field))
            {
                holds.Add(
                    $"{path}: search.{name}.{find.Field} has not been resolved against the schema search '{name}' pins, so no query can be written for it; the mapping's preflight says why");
                return null;
            }

            var kind = search!.Declaration.Kind;

            // A value that already is an OSDU id names its record, so nothing is asked; an id of another entity type
            // would be a reference to the wrong kind of record, which no search would have returned.
            if (OsduId().IsMatch(value))
            {
                var entityType = value.Split(':')[1];
                var searchedType = kind.Split(':')[2];
                if (string.Equals(entityType, searchedType, StringComparison.Ordinal))
                {
                    return WithVersionSeparator(value);
                }

                holds.Add($"{path}: '{value}' is already an OSDU id, of a {entityType} record, and search.{name} looks for {searchedType} records ({kind})");
                return null;
            }

            OsduQuery query;
            try
            {
                query = OsduQuery.Equal(field!, value);
            }
            catch (OsduQueryException ex)
            {
                // The value cannot be asked for on this line; a later line may still find the record, and if none does the
                // record is held, since nobody knows whether the platform holds it.
                unaskable = true;
                outcomes.Add($"{find.Field} '{value}' cannot be searched for: {ex.Message}");
                continue;
            }

            var question = new SearchQuestion(kind, find.Field, value, query.Text);
            if (!renderer.Search.TryAnswer(question, out var answer))
            {
                // Not asked yet. The render stops here rather than guessing, and the caller asks and renders again.
                searched.Ask(question);
                return null;
            }

            searched.Use(question, answer);
            switch (answer.Outcome)
            {
                case SearchOutcome.Found:
                    return WithVersionSeparator(answer.Id!);
                case SearchOutcome.NotFound:
                    break;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' {answer.Describe()}; a reference picked from several, or taken without an answer, would deliver a wrong document, so the record is held. Make the incoming value name one record.");
                    return null;
            }

            // Nothing holds the value exactly. Where the partition's indexer keeps a lowercased copy of text, the same value
            // is asked for regardless of case, and taken only when that finds exactly one record, which is the rule a cache
            // lookup keeps: codes that differ only by case are different records, so several answers select none of them.
            if (Caseless(renderer, field!, value) is not { } loose)
            {
                outcomes.Add($"{find.Field} '{value}' {answer.Describe()}");
                continue;
            }

            var looseQuestion = new SearchQuestion(kind, find.Field, value, loose.Text);
            if (!renderer.Search.TryAnswer(looseQuestion, out var looseAnswer))
            {
                searched.Ask(looseQuestion);
                return null;
            }

            searched.Use(looseQuestion, looseAnswer);
            switch (looseAnswer.Outcome)
            {
                case SearchOutcome.Found:
                    return WithVersionSeparator(looseAnswer.Id!);
                case SearchOutcome.NotFound:
                    outcomes.Add($"{find.Field} '{value}' found no record, exactly or once case is ignored");
                    continue;
                case SearchOutcome.Refused:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' found no record exactly, and once case is ignored it {looseAnswer.Describe()}; a reference taken without an answer would deliver a wrong document, so the record is held.");
                    return null;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' found no record exactly, and once case is ignored it {looseAnswer.Describe()}; a reference picked from several would deliver a wrong document, so the record is held. Make the incoming value exact with a replace modifier.");
                    return null;
            }
        }

        if (outcomes.Count == 0)
        {
            if (entry.Required)
            {
                var operands = string.Join(", ", entry.FindBy.Where(f => f.Column is not null).Select(f => f.Column!.ToString()).Distinct(StringComparer.Ordinal));
                holds.Add($"{path}: {operands} is empty, so there is nothing to search for, and the entry is required");
            }

            return null;
        }

        var searchedKind = renderer.Searches.Searches.TryGetValue(name, out var declared) ? declared.Declaration.Kind : name;
        var reason = $"{path}: no {name} on the platform ({searchedKind}) matches: {string.Join("; ", outcomes)}";
        if (unaskable)
        {
            holds.Add(reason + ". A value that cannot be searched for proves nothing about whether the record exists, so the record is held whatever the entry's required flag says");
            return null;
        }

        return Missing(entry, reason, holds);
    }

    /// <summary>
    /// The query that asks for <paramref name="value"/> regardless of case, or null when there is none to ask: the system
    /// properties the render is pinned to do not say the partition's indexer keeps a lowercased copy of text, the property
    /// is not text, or the value cannot be asked for that way. Only a system property known to be on changes how a lookup
    /// is written, so a partition whose settings were never read is asked exact questions alone.
    /// </summary>
    private static OsduQuery? Caseless(MappingRenderer renderer, OsduField field, string value)
    {
        if (!renderer.Context.KeywordLower || field.Index != OsduFieldIndex.Text)
        {
            return null;
        }

        try
        {
            return OsduQuery.Equal(field, value, caseInsensitive: true);
        }
        catch (OsduQueryException)
        {
            // The exact question was asked and answered; the only value the lowercased copy refuses beyond it is a
            // spelling of the text null, which that copy holds for every record with no value.
            return null;
        }
    }

    /// <summary>
    /// Resolves a cache source: each findBy line in turn compares its cached field with its value, and the first line that
    /// finds exactly one record wins. The value an entry matched by and the value it read are both recorded as
    /// dependencies of the render, so a later cache version can tell which records it changes.
    /// </summary>
    private static object? Cached(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var path = entry.Target.Text;
        var typeName = entry.Source!.CacheType!;
        var type = renderer.References.Type(typeName);
        if (type is null)
        {
            holds.Add($"{path}: the cache holds no type '{typeName}' in {CacheLabel(renderer.Context)}");
            return null;
        }

        var located = Locate(entry, type, root, item, renderer, holds, usages);
        return located.Item is { } hit ? Select(entry, type, hit, renderer, holds, usages) : located.Written;
    }

    /// <summary>
    /// What a cache node's findBy lines find: the one record the first line that finds one names, or an id the cache does
    /// not hold that the node writes as it is (<see cref="Located.Written"/>), or nothing, with the reason held or left out
    /// as the node's required flag says. Several records answering to a value hold the record whatever the flag says.
    /// </summary>
    private static Located Locate(
        MappingEntry entry, ReferenceType type, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var path = entry.Target.Text;
        var source = entry.Source!;
        var typeName = type.Name;
        var version = CacheLabel(renderer.Context);
        var tried = new List<(FindBy Find, string Value)>();
        (FindBy Find, string Value, ReferenceMatch Found)? undecided = null;
        foreach (var find in entry.FindBy)
        {
            string? value;
            if (find.Literal is not null)
            {
                value = find.Literal;
            }
            else
            {
                if (!TryModify(entry, Read(find.Column!, root, item), root, item, renderer, holds, usages, out var modified))
                {
                    return Located.Nothing;
                }

                value = modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified);
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            tried.Add((find, value));

            // A value that already is an OSDU id names its record without a lookup, found by its record id whatever the type
            // caches under a field called ID. A lookup table holds no OSDU records, so there a value of that shape is matched
            // like any other.
            if (!type.IsLookup && OsduId().IsMatch(value))
            {
                if (CachedReferences.Parse(value) is { } named && type.ById(named.Id) is { } byId)
                {
                    usages.Add(new CacheUsage(typeName, byId.Id, "id", value, CacheUsageKind.Match));
                    return new Located(byId, null);
                }

                if (source.ReadsRecordId)
                {
                    return new Located(null, WithVersionSeparator(value));
                }

                Missing(entry, $"{path}: '{value}' is already an OSDU id that {version} does not hold, so '{source.CacheField}' cannot be read from the cache", holds);
                return Located.Nothing;
            }

            var found = type.Find(find.Field, value, entry.IgnoreSeparators);
            if (found.Item is { } hit)
            {
                usages.Add(new CacheUsage(typeName, hit.Id, ReferenceField.Normalize(find.Field), value, CacheUsageKind.Match));
                return new Located(hit, null);
            }

            if (found.IsCaseAmbiguous)
            {
                undecided ??= (find, value, found);
            }
        }

        if (undecided is { } choice)
        {
            // Several records answer to the value. Taking the first would deliver the wrong reference without a trace, so
            // the record is held whatever the required flag says, and the reason names every candidate.
            var candidates = string.Join(", ", choice.Found.CaseVariants.Select(c => c.Id));
            holds.Add(
                $"{path}: '{choice.Value}' matches {choice.Found.CaseVariants.Count} {typeName} records by {ReferenceField.Normalize(choice.Find.Field)} {choice.Found.Loosening} ({candidates}) in {version}; make the incoming value exact with a replace modifier");
            return Located.Nothing;
        }

        if (tried.Count == 0)
        {
            var operands = string.Join(", ", entry.FindBy.Where(f => f.Column is not null).Select(f => f.Column!.ToString()).Distinct(StringComparer.Ordinal));
            if (entry.Required)
            {
                holds.Add($"{path}: {operands} is empty, so there is nothing to find in the cache, and the entry is required");
            }

            return Located.Nothing;
        }

        if (!type.IsLookup)
        {
            // Each value no record answered to is recorded, so a later version holding a record it finds (a wellbore loaded
            // after the logs that name it) reaches the records built without one.
            foreach (var (find, value) in tried)
            {
                usages.Add(new CacheUsage(typeName, CacheUsage.ListingKey(value), ReferenceField.Normalize(find.Field), value, CacheUsageKind.Unlisted));
            }
        }

        var fold = entry.IgnoreSeparators ? ", even with punctuation and spacing ignored" : string.Empty;
        var described = tried.Select(t => t.Value).Distinct(StringComparer.Ordinal).Count() == 1
            ? $"'{tried[0].Value}' by {string.Join("/", tried.Select(t => ReferenceField.Normalize(t.Find.Field)))}"
            : string.Join(" or ", tried.Select(t => $"{ReferenceField.Normalize(t.Find.Field)} '{t.Value}'"));
        Missing(entry, $"{path}: no {typeName} matches {described}{fold} in {version}", holds);
        return Located.Nothing;
    }

    /// <summary>
    /// What a cache node's lines found: the record they name, or an id the node writes without a record the cache holds,
    /// or neither.
    /// </summary>
    private readonly record struct Located(ReferenceItem? Item, object? Written)
    {
        public static Located Nothing => default;
    }

    /// <summary>
    /// The value of a list of values: what each item gives, one after another in the order the list writes them, a value
    /// given twice (whatever its case) written once, where it is first given. An item that gives nothing adds nothing; one
    /// that holds holds the record, as it would on its own. A list none of whose items gives a value is left out.
    /// </summary>
    private static JsonNode? Listed(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var values = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in entry.Parts)
        {
            if (Evaluate(part, root, item, renderer, holds, usages, searched) is not { } given)
            {
                continue;
            }

            foreach (var value in given is JsonArray set ? set.ToList() : [given])
            {
                if (value is null)
                {
                    continue;
                }

                var key = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text.Trim() : CanonicalJson.ToString(value);
                if (seen.Add(key))
                {
                    values.Add(value.DeepClone());
                }
            }
        }

        return values.Count > 0 ? values : null;
    }

    /// <summary>
    /// The value of an item of a list of objects: the object its properties give, each written at its place in the item as
    /// the record's own properties are, or null when none of them gives one, which leaves the item out of the list. A list
    /// the item defines and that gives nothing is written empty in an item that is written. A property that holds holds the
    /// record, as it would anywhere in it.
    /// </summary>
    private static JsonNode? Assembled(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var written = new JsonObject();
        List<MappingEntry>? empty = null;
        foreach (var property in entry.Properties)
        {
            var held = holds.Count;
            var waiting = searched.Unanswered.Count;
            if (Evaluate(property, root, item, renderer, holds, usages, searched) is { } value)
            {
                MappingRenderer.SetPath(written, property.Target.WithinItem, value);
            }
            else if (holds.Count == held && searched.Unanswered.Count == waiting && renderer.WritesList(property))
            {
                (empty ??= []).Add(property);
            }
        }

        if (written.Count == 0)
        {
            return null;
        }

        foreach (var property in empty ?? [])
        {
            MappingRenderer.SetEmptyList(written, property.Target.WithinItem);
        }

        return written;
    }

    /// <summary>
    /// The value of a <c>$findAll</c> node: the field it reads from every row of its type whose key field holds a value its
    /// operand gives and whose every field the node asks to be empty holds nothing, as a list in the order of the rows' ids,
    /// a value two rows give written once. Every key is recorded with the rows it found, none included, and so is every
    /// field a row was read or passed over for, so a later version that lists another row under a key, drops one, or
    /// changes what one gives, reaches every record built from it. A key that is an OSDU reference finds the rows that name
    /// the record with or without the version separator, since both name the same record.
    /// </summary>
    private static object? CachedAll(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var path = entry.Target.Text;
        var query = entry.FindAll!;
        var source = entry.Source!;
        var version = CacheLabel(renderer.Context);
        var type = renderer.References.Type(query.Type);
        if (type is null)
        {
            holds.Add($"{path}: the cache holds no type '{query.Type}' in {version}");
            return null;
        }

        if (!TryKeys(entry, query.Operand, root, item, renderer, holds, usages, out var keys))
        {
            return null;
        }

        if (keys.Count == 0)
        {
            return Missing(entry, $"{path}: {query.Operand} gives no value, so no {type.Name} row is found by {ReferenceField.Normalize(query.Field)}", holds);
        }

        var field = ReferenceField.Normalize(query.Field);
        var rows = new List<ReferenceItem>();
        foreach (var key in keys.SelectMany(KeyForms).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = type.FindAll(query.Field, key);
            usages.Add(CacheUsage.Listing(type.Name, field, key, found.Select(row => row.Id)));
            foreach (var row in found)
            {
                if (!rows.Contains(row) && Passes(type, row, query.Empty, usages))
                {
                    rows.Add(row);
                }
            }
        }

        var values = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(row => row.Id, StringComparer.Ordinal))
        {
            if (source.ReadsRecordId && !type.IsLookup)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, "id", row.Id, CacheUsageKind.Value));
                Add(JsonValue.Create(WithVersionSeparator(row.Id)));
                continue;
            }

            if (type.Value(row, source.CacheField!) is not { } cached)
            {
                // Built without a value here, so a later version giving the row one changes the record.
                usages.Add(new CacheUsage(type.Name, row.Id, ReferenceField.Normalize(source.CacheField!), string.Empty, CacheUsageKind.Empty));
                continue;
            }

            usages.Add(new CacheUsage(type.Name, row.Id, ReferenceField.Normalize(source.CacheField!), cached.Text, CacheUsageKind.Value));
            foreach (var value in cached.Node is JsonArray set ? set.ToList() : [cached.Node])
            {
                if (value is not null)
                {
                    Add(value);
                }
            }
        }

        if (values.Count > 0)
        {
            return values;
        }

        var keysText = string.Join(", ", keys.Select(k => $"'{k}'"));
        var narrowed = query.Empty.Count == 0 ? string.Empty : $" with {string.Join(" and ", query.Empty.Select(f => $"{ReferenceField.Normalize(f)} empty"))}";
        return Missing(entry, rows.Count == 0
            ? $"{path}: no {type.Name} row{narrowed} holds {keysText} under {field} in {version}"
            : $"{path}: the {rows.Count} {type.Name} row(s) holding {keysText} under {field} give nothing at '{source.CacheField}' in {version}", holds);

        void Add(JsonNode value)
        {
            var key = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text.Trim() : CanonicalJson.ToString(value);
            if (key.Length > 0 && seen.Add(key))
            {
                values.Add(value.DeepClone());
            }
        }
    }

    /// <summary>
    /// The keys a <c>$findAll</c> operand gives for the row: a literal, a column's value after the node's modifiers, or every
    /// value the path of a lookup's record holds. False when finding them held the record: a column a modifier refused, or
    /// a lookup several records answer to, which holds the record whatever the node's required flag says.
    /// </summary>
    private static bool TryKeys(
        MappingEntry entry, FindAllOperand operand, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, out List<string> keys)
    {
        keys = [];
        if (operand.Literal is { } literal)
        {
            keys.Add(literal.Trim());
            return true;
        }

        if (operand.Column is { } column)
        {
            if (!TryModify(entry, Read(column, root, item), root, item, renderer, holds, usages, out var modified))
            {
                return false;
            }

            var text = (modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified))?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                keys.Add(text);
            }

            return true;
        }

        var lookup = operand.Lookup!;
        var type = renderer.References.Type(lookup.CacheType);
        if (type is null)
        {
            holds.Add($"{entry.Target.Text}: lookup {lookup.Name} reads {lookup.CacheType}, and the cache holds no type '{lookup.CacheType}' in {CacheLabel(renderer.Context)}");
            return false;
        }

        // The lookup is asked as the optional node it would be on its own: finding no record gives no keys, and the node
        // that reads them says what that means.
        var reader = new MappingEntry
        {
            Index = entry.Index,
            Location = entry.Location,
            Target = entry.Target,
            Source = new MappingSource { Kind = MappingSourceKind.Cache, CacheType = lookup.CacheType, CacheField = operand.LookupPath, Lookup = lookup.Name },
            FindBy = lookup.FindBy,
            Modifiers = lookup.Modifiers,
            IgnoreSeparators = lookup.IgnoreSeparators,
            Required = false,
        };
        var mistakes = new List<string>();
        var located = Locate(reader, type, root, item, renderer, mistakes, usages);
        if (mistakes.Count > 0)
        {
            holds.AddRange(mistakes);
            return false;
        }

        if (located.Item is not { } record)
        {
            // An id the cache does not hold names its record by itself, and only its id can be read from it.
            if (located.Written is string id && ReferenceField.IsId(operand.LookupPath!))
            {
                keys.Add(id);
            }

            return true;
        }

        var field = ReferenceField.Normalize(operand.LookupPath!);
        if (ReferenceField.IsId(field) && type.MeansRecordId(field))
        {
            usages.Add(new CacheUsage(type.Name, record.Id, "id", record.Id, CacheUsageKind.Value));
            keys.Add(record.Id);
            return true;
        }

        if (type.Value(record, field) is not { } value || value.Terms.Count == 0)
        {
            // Built without a value here, so a later version giving the record one (a wellbore given its field) changes it.
            usages.Add(new CacheUsage(type.Name, record.Id, field, string.Empty, CacheUsageKind.Empty));
            return true;
        }

        usages.Add(new CacheUsage(type.Name, record.Id, field, value.Text, CacheUsageKind.Value));
        keys.AddRange(value.Terms);
        return true;
    }

    /// <summary>
    /// The forms a key finds rows by: the key, and for an OSDU reference the same reference with and without the separator
    /// before its version, since <c>dev:master-data--Field:1234:</c> and <c>dev:master-data--Field:1234</c> name one record.
    /// </summary>
    private static IEnumerable<string> KeyForms(string key)
    {
        yield return key;
        if (!OsduId().IsMatch(key) || CachedReferences.Parse(key) is not { } reference)
        {
            yield break;
        }

        var bare = reference.Id;
        if (!string.Equals(bare, key, StringComparison.Ordinal))
        {
            yield return bare;
        }

        var written = WithVersionSeparator(bare);
        if (!string.Equals(written, key, StringComparison.Ordinal))
        {
            yield return written;
        }
    }

    /// <summary>
    /// Whether a row a <c>$findAll</c> key found holds nothing under every field the node asks to be empty. Each field a row
    /// passed on is recorded as read empty, and the first it failed on with what it held, so a later version that fills one
    /// or empties the other reaches the record.
    /// </summary>
    private static bool Passes(ReferenceType type, ReferenceItem row, IReadOnlyList<string> empty, List<CacheUsage> usages)
    {
        foreach (var field in empty)
        {
            var name = ReferenceField.Normalize(field);
            if (type.HoldsNothing(row, field))
            {
                usages.Add(new CacheUsage(type.Name, row.Id, name, string.Empty, CacheUsageKind.Empty));
                continue;
            }

            usages.Add(new CacheUsage(type.Name, row.Id, name, type.Value(row, field)!.Text, CacheUsageKind.Value));
            return false;
        }

        return true;
    }

    /// <summary>The cache version a render read, as a hold reason names it: "version 20260910T165153Z of the cache of partition 'dev'".</summary>
    private static string CacheLabel(RenderContext context)
        => context.CacheScope is null ? $"cache version {context.CacheVersion}" : $"version {context.CacheVersion} of the cache of partition '{context.CacheScope}'";

    private static object? Select(MappingEntry entry, ReferenceType type, ReferenceItem hit, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var source = entry.Source!;
        if (source.ReadsRecordId && type.IsLookup)
        {
            // A lookup row's id is its key, not an OSDU record id, and writing it as a reference would point at nothing. The
            // gate refuses this mapping; a render that meets it anyway holds, whatever the entry's required flag says.
            holds.Add(
                $"{entry.Target.Text}: {type.Name} is a lookup table in {CacheLabel(renderer.Context)}, whose rows are not OSDU records, so it has no id to write; read one of its fields ({string.Join(", ", type.FieldNames)})");
            return null;
        }

        if (source.ReadsRecordId)
        {
            usages.Add(new CacheUsage(type.Name, hit.Id, "id", hit.Id, CacheUsageKind.Value));
            return WithVersionSeparator(hit.Id);
        }

        var field = source.CacheField!;
        if (type.Value(hit, field) is not { } cached)
        {
            // The document is built without the value, so a later version giving the record one changes it.
            usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), string.Empty, CacheUsageKind.Empty));
            return Missing(
                entry,
                $"{entry.Target.Text}: {type.Name} '{hit.Id}' caches nothing at '{field}' in {CacheLabel(renderer.Context)}. Cached: {string.Join(", ", type.FieldNames.Prepend("id"))}",
                holds);
        }

        usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), cached.Text, CacheUsageKind.Value));
        if (TakesReference(renderer, entry))
        {
            return References(entry, type, hit, field, cached, renderer, holds, usages);
        }

        return cached.Node.DeepClone();
    }

    /// <summary>Whether the entry's variable is a relationship, or a list of them: what it takes is an OSDU record id.</summary>
    private static bool TakesReference(MappingRenderer renderer, MappingEntry entry)
        => renderer.Schema.Resolve(entry.Target.SchemaPath) is { } property
            && (property.IsRelationship || property.Items?["x-osdu-relationship"] is JsonArray);

    /// <summary>
    /// A cached field written to a relationship: OSDU's own translations cache the id of the record a value stands for
    /// (<c>ExternalUnitOfMeasure.UnitOfMeasureID</c>), and the mapping writes it as the reference, with the colon that
    /// separates a version added where the cached id leaves it out. Where the cache holds records of the entity type an
    /// id names, the record it names is one of them, and it is recorded among the render's dependencies, so a version
    /// that drops it reaches this record.
    /// </summary>
    /// <remarks>
    /// A value that is not an OSDU record id, or one naming a record the cache holds that type of and not that record,
    /// holds the record whatever the entry's required flag says: writing it would put a reference to nothing into OSDU.
    /// The gate refuses a mapping whose cached field holds such values; this holds a render that meets one anyway.
    /// </remarks>
    private static object? References(
        MappingEntry entry, ReferenceType type, ReferenceItem hit, string field, ReferenceValue cached, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages)
    {
        var path = entry.Target.Text;
        var version = CacheLabel(renderer.Context);
        var written = new List<JsonNode?>();
        foreach (var node in cached.Node is JsonArray set ? set.ToList() : [cached.Node])
        {
            var text = node is JsonValue value && value.TryGetValue<string>(out var held) ? held.Trim() : null;
            if (CachedReferences.Parse(text) is not { } reference)
            {
                holds.Add(
                    $"{path}: {type.Name} '{hit.Id}' holds {Clip(node?.ToJsonString() ?? "null")} at '{field}' in {version}, which is not an OSDU record id, and {path} takes a reference to a record");
                return null;
            }

            var holding = CachedReferences.Holding(renderer.References, reference.EntityType);
            if (holding.Count > 0)
            {
                if (CachedReferences.Find(holding, reference) is not { } found)
                {
                    holds.Add(
                        $"{path}: {type.Name} '{hit.Id}' names {reference.Id} at '{field}', and {version} holds no such {reference.EntityType} record in {string.Join(", ", holding.Select(t => t.Name))}, so the reference would point at nothing");
                    return null;
                }

                usages.Add(new CacheUsage(found.Type.Name, found.Item.Id, "id", found.Item.Id, CacheUsageKind.Match));
            }

            written.Add(JsonValue.Create(CachedReferences.Written(text!)));
        }

        return cached.Node is JsonArray ? new JsonArray(written.ToArray()) : written[0];
    }

    /// <summary>A value the cache does not have: a required entry holds the record, an optional one leaves the variable out.</summary>
    private static object? Missing(MappingEntry entry, string reason, List<string> holds)
    {
        if (entry.Required)
        {
            holds.Add(reason);
        }

        return null;
    }

    private static string WithVersionSeparator(string id) => id.EndsWith(':') ? id : id + ":";

    private static JsonNode Expand(JsonNode node, MappingRenderer renderer) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(MappingEntry.ExpandParameters(text, renderer.ParameterValue)),
        JsonObject obj => new JsonObject(obj.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value is null ? null : Expand(kv.Value, renderer)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Expand(item, renderer)).ToArray()),
        _ => node.DeepClone(),
    };

    /// <summary>
    /// Converts a value to the type its variable declares: text becomes a number, an integer or a boolean where the schema
    /// says so, a single value bound to a list becomes a list of one, and a set of values fills a list or holds a single
    /// value. A date is written in the form the variable's format takes. A value that cannot take the type holds the record
    /// with a reason naming the target.
    /// </summary>
    private static JsonNode? Convert(object raw, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (raw is JsonArray set)
        {
            return ConvertSet(set, property, path, holds);
        }

        if (raw is JsonObject obj)
        {
            if (type is SchemaType.Object or SchemaType.Any)
            {
                return obj;
            }

            // An object bound to a list of objects is a list of one, as a single value bound to a list of values is: a
            // literal item of a list of objects, or a cached field holding one object.
            if (type == SchemaType.Array && property?.ItemScalarType is SchemaType.Object)
            {
                return new JsonArray(obj.DeepClone());
            }

            holds.Add($"{path}: an object cannot be written where the template takes a {Name(type)}");
            return null;
        }

        var scalar = raw is JsonValue value ? Native(value) : raw;
        if (type == SchemaType.Array && property?.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var single = Scalar(scalar, itemType, property?.ItemFormat, path, holds);
            return single is null ? null : new JsonArray(single);
        }

        return Scalar(scalar, type, property?.Format, path, holds);
    }

    private static JsonNode? ConvertSet(JsonArray set, SchemaProperty? property, string path, List<string> holds)
    {
        var type = property?.Type ?? SchemaType.Any;
        if (type is SchemaType.Array && property?.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var items = new JsonArray();
            foreach (var element in set)
            {
                if (element is not null && Scalar(Native(element), itemType, property?.ItemFormat, path, holds) is { } converted)
                {
                    items.Add(converted);
                }
            }

            return items.Count > 0 ? items : null;
        }

        if (type is SchemaType.Array or SchemaType.Object or SchemaType.Any)
        {
            return set.DeepClone();
        }

        if (set.Count == 1 && set[0] is { } only)
        {
            return Scalar(Native(only), type, property?.Format, path, holds);
        }

        holds.Add($"{path}: {set.Count} values were given but the template takes one {Name(type)}; read one value or fill a list");
        return null;
    }

    private static JsonNode? Scalar(object raw, SchemaType type, string? format, string path, List<string> holds)
    {
        if (DateValues.IsDate(raw) && type is SchemaType.String or SchemaType.Any)
        {
            var written = DateValues.Write(raw, format, out var dateProblem);
            if (written is null)
            {
                holds.Add($"{path}: {dateProblem}");
                return null;
            }

            return JsonValue.Create(written);
        }

        // A number is written in the form its property takes: a number, an integer in its format's range, or its shortest text.
        string? problem;
        switch (type)
        {
            case SchemaType.Number:
                return Written(NumberValues.ToNumber(raw, out problem), problem, path, holds);
            case SchemaType.Integer:
                return Written(NumberValues.ToInteger(raw, format, out problem), problem, path, holds);
            case SchemaType.Any when NumberValues.IsNumber(raw):
                return Written(NumberValues.ToNumber(raw, out problem), problem, path, holds);
            case SchemaType.String when NumberValues.IsNumber(raw):
                return Written(NumberValues.ToText(raw, out problem) is { } text ? JsonValue.Create(text) : null, problem, path, holds);
        }

        try
        {
            switch (type)
            {
                case SchemaType.String:
                    return JsonValue.Create(SourceRow.Stringify(raw));
                case SchemaType.Boolean:
                    return raw switch
                    {
                        bool b => JsonValue.Create(b),
                        _ => JsonValue.Create(ParseBool(SourceRow.Stringify(raw)!)),
                    };
                case SchemaType.Object:
                case SchemaType.Array:
                    throw new FormatException($"a single value cannot be written where the template takes a {Name(type)}");
                default:
                    return JsonValue.Create(Clr(raw));
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            holds.Add($"{path}: value '{SourceRow.Stringify(raw)}' is not a valid {Name(type)} ({ex.Message})");
            return null;
        }
    }

    /// <summary>A converted value, or the reason it could not be converted added as a hold naming the target.</summary>
    private static JsonNode? Written(JsonNode? value, string? problem, string path, List<string> holds)
    {
        if (value is null)
        {
            holds.Add($"{path}: {problem}");
        }

        return value;
    }

    /// <summary>A JSON value as the CLR value the conversions expect, so a cached or static value converts like a dataset value.</summary>
    private static object Native(JsonNode node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            JsonValueKind.Number => value.TryGetValue<long>(out var whole) ? whole : value.GetValue<double>(),
            _ => node,
        }
        : node;

    /// <summary>A value that is not a number or a date, for a variable the template does not type: text and booleans as they are, anything else as its text.</summary>
    private static object Clr(object raw) => raw is string or bool ? raw : SourceRow.Stringify(raw)!;

    private static bool ParseBool(string text)
    {
        var t = text.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t is "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t is "0" || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new FormatException($"'{text}' is not a boolean");
    }

    private static string Name(SchemaType type) => type.ToString().ToLowerInvariant();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+--[\w\-\.]+:[\w\-\.\:\%]+$")]
    private static partial Regex OsduId();
}
