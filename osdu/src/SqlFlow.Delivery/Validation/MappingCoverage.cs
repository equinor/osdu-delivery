using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>Whether what a mapping writes reaches a variable on every row, on some rows, or never.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoverageState>))]
public enum CoverageState
{
    /// <summary>Nothing fills it, and nothing fills anything it holds.</summary>
    Empty,

    /// <summary>What fills it may leave it out: <c>required: false</c>, or an <c>appliesWhen</c> that does not hold on every row.</summary>
    Sometimes,

    /// <summary>It is filled on every row: a static value, or a required entry that always applies.</summary>
    Always,
}

/// <summary>How a mapping fills one variable of the template it pins.</summary>
/// <param name="Target">The variable's path, <c>osdu.data.FacilityName</c>.</param>
/// <param name="State">Whether the mapping reaches it on every row, on some rows, or never.</param>
/// <param name="Direct">True when an entry targets the variable itself; false for a holder filled through what it holds.</param>
/// <param name="Required">Whether the schema requires the property in the object that holds it.</param>
/// <param name="WrittenBy">
/// For a variable no entry targets that an entry further up writes: the target of that entry. Either its literal holds the
/// variable (the TechnicalAssuranceTypeID of the items of a literal TechnicalAssurances list, or of a <c>$coalesce</c>'s
/// literal alternative), or it writes an object whole from a value only the render knows (a cached field holding an
/// object), whose properties are whatever that value holds. Null otherwise.
/// </param>
/// <param name="Values">
/// With <paramref name="WrittenBy"/>: the values a literal gives the variable, once each in the order it holds them (one
/// per item of a literal list that carries a different one), each a text, a number or a boolean as written. Empty for an
/// object or a list the literal writes there, whose own properties say what they hold, and for a variable an object
/// written whole holds.
/// </param>
public sealed record VariableCoverage(
    string Target, CoverageState State, bool Direct, bool Required, string? WrittenBy = null, IReadOnlyList<string>? Values = null);

/// <summary>What a mapping fills of the template it pins, variable by variable, and what it leaves required and empty.</summary>
/// <param name="Variables">Every variable a mapping may fill, in template order, parents before their children.</param>
/// <param name="Issues">What the mapping leaves required and empty. The errors are the ones the delivery gate raises.</param>
public sealed record CoverageReport(IReadOnlyList<VariableCoverage> Variables, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// What a mapping covers of its template. The gate's own rule on required properties lives here
/// (<see cref="RequiredIssues"/>, which <see cref="Preflight"/> runs as its check 5), so what a view of the template shows
/// and what stops a delivery are one rule, never two that can drift apart.
/// </summary>
public static class MappingCoverage
{
    /// <summary>
    /// Whether an entry writes its target on every row: a static value, a <c>$coalesce</c> whose last alternative is one,
    /// or a required entry that always applies. An entry that may be left out (<c>required: false</c>) or that only
    /// applies to some rows (<c>appliesWhen</c>) does not.
    /// </summary>
    public static bool FillsEveryRow(MappingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.AppliesWhen is null && (entry.IsStatic || entry.Required || HasDefault(entry));
    }

    /// <summary>True for a <c>$coalesce</c> node whose last alternative is a literal: it gives a value whenever it applies.</summary>
    private static bool HasDefault(MappingEntry entry) => entry.IsCoalesce && entry.Alternatives[^1].IsStatic;

    /// <summary>
    /// The gate's rule: every property the schema requires of <c>data</c> has an entry that is allowed to be empty only if
    /// the schema allows it. An object the schema requires (a WellboreTrajectory's VerticalMeasurement) is filled by the
    /// entries for its properties when it has no entry of its own, and one of them has to render on every row: a static
    /// value, or a required entry without appliesWhen, which holds the record when its value is empty.
    /// </summary>
    /// <param name="mapping">The mapping to judge.</param>
    /// <param name="schema">The template version it pins.</param>
    /// <param name="where">What the messages name the mapping by: its path in the repository, else its reference.</param>
    public static IReadOnlyList<ValidationIssue> RequiredIssues(MappingDefinition mapping, SchemaSnapshot schema, string where)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);

        var issues = new List<ValidationIssue>();
        foreach (var required in schema.RequiredAt("data"))
        {
            var target = TemplatePath.TextOf(RecordPathNotation.Append("data", required));
            var entry = mapping.Entries.FirstOrDefault(e => e.Target.Text == target);
            if (entry is not null)
            {
                if (!entry.IsStatic && !entry.Required && !HasDefault(entry))
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {entry.Where} is $required: false, but the template requires {target}, so a record without it cannot be sent.", target));
                }

                continue;
            }

            var properties = mapping.Entries.Where(e => RecordPathNotation.Below(e.Target.Text, target) is { } rest && !rest.StartsWith("[]", StringComparison.Ordinal)).ToList();
            if (properties.Count == 0)
            {
                issues.Add(ValidationIssue.Error($"{where}: template {mapping.Template.Kind} requires {target}, which the mapping does not fill.", target));
            }
            else if (!properties.Any(FillsEveryRow))
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: template {mapping.Template.Kind} requires {target}, and every entry filling its properties ({string.Join(", ", properties.Select(p => p.Where))}) "
                    + $"may leave it out ($required: false or $when), so a record without it cannot be sent. Make one of them required, or fill {target} itself.",
                    target));
            }
        }

        return issues;
    }

    /// <summary>
    /// What the mapping fills of the template, variable by variable, with what it leaves required and empty. The gate's
    /// errors (<see cref="RequiredIssues"/>) come first and are never contradicted; every other required variable the
    /// mapping leaves empty is a warning, because the gate does not stop a delivery for it.
    /// </summary>
    /// <remarks>
    /// A required variable inside an object nothing fills is not reported: the record holds no such object, so nothing is
    /// missing from it. Only the variables a mapping may fill are covered; what OSDU Delivery writes, what OSDU sets, and a
    /// list inside a repeated item are left out, because no mapping fills those. What each variable shows is rolled up
    /// from what it holds (<see cref="Rolled"/>); what the findings are judged on is what the mapping itself writes.
    /// </remarks>
    public static CoverageReport Of(MappingDefinition mapping, OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(template);
        var where = mapping.SourcePath ?? mapping.Reference.ToString();

        // What each entry writes, and what that makes of every object on the way to it: a holder is reached by the best of
        // what it holds, so an object with no entry of its own still reads as filled when its properties are filled. A
        // literal object or list writes the properties it holds as well (a TechnicalAssurances list whose item names its
        // TechnicalAssuranceTypeID), so those are written wherever the literal is, without an entry of their own.
        var own = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        var inside = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        var direct = new HashSet<string>(StringComparer.Ordinal);
        var literals = new Dictionary<string, (string Holder, List<string> Values)>(StringComparer.Ordinal);
        foreach (var entry in mapping.Entries)
        {
            var state = FillsEveryRow(entry) ? CoverageState.Always : CoverageState.Sometimes;
            own[entry.Target.Text] = Best(own, entry.Target.Text, state);
            direct.Add(entry.Target.Text);
            var walk = new LiteralWalk(own, literals, entry.Target.Text);
            if (entry.Parts.Any(part => part.IsObject))
            {
                WrittenByItems(walk, entry, state);
            }
            else if (entry.IsStatic && entry.Static is { } literal)
            {
                WrittenBy(walk, entry.Target.Text, [literal], state);
            }
            else if (!entry.IsRepeater)
            {
                // A $coalesce's literal alternative writes what it holds on the rows it is the one taken.
                foreach (var alternative in entry.Alternatives.Where(a => a.Static is not null))
                {
                    WrittenBy(walk, entry.Target.Text, [alternative.Static!], CoverageState.Sometimes);
                }

                // An object or a list written whole from a value only the render knows (a cached field holding one) holds
                // whatever that value holds: its properties are written by the entry, on the rows the value holds them.
                if (template.Find(entry.Target) is { Shape: TemplateVariableShape.Group or TemplateVariableShape.GroupList }
                    && entry.ValueNodes.Any(node => node.Static is null))
                {
                    WrittenWhole(walk, template, entry.Target.Text);
                }
            }

            for (var holder = entry.Target.Parent; holder is not null; holder = holder.Parent)
            {
                inside[holder.Text] = Best(inside, holder.Text, state);
            }
        }

        // Whether the mapping reaches a variable at all, by itself or through anything it holds. This is what the
        // findings are judged on, so a required variable is missing when nothing in the document writes it.
        var states = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        var listed = new List<(string Path, string? Holder, bool Required)>();
        foreach (var variable in template.Fillable)
        {
            var path = variable.Path.Text;
            states[path] = Better(own.GetValueOrDefault(path, CoverageState.Empty), inside.GetValueOrDefault(path, CoverageState.Empty));
            listed.Add((path, variable.Path.Parent?.Text, variable.Required));
        }

        // A free key of an object that takes them (osdu.tags.DeliveredBy) and a path inside an open object
        // (osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit) are no variables of the template, but targets the
        // mapping fills, so they are covered the same way and a view can show them under the object that holds them. Inside
        // an open object the objects between it and the target are listed first, so a holder always comes before what it
        // holds, which is the order the roll-up reads.
        foreach (var entry in mapping.Entries)
        {
            var target = entry.Target.Text;
            if (states.ContainsKey(target) || template.Find(entry.Target) is not { Role: TemplateVariableRole.Mapping, Nested: false } key)
            {
                continue;
            }

            if (key.Inside is { } open)
            {
                var between = new List<TemplatePath>();
                for (var holder = entry.Target.Parent; holder is not null && !holder.Equals(open); holder = holder.Parent)
                {
                    between.Add(holder);
                }

                for (var i = between.Count - 1; i >= 0; i--)
                {
                    var holder = between[i];
                    if (states.TryAdd(holder.Text, Reach(holder.Text)))
                    {
                        listed.Add((holder.Text, holder.Parent?.Text, false));
                    }
                }
            }

            states[target] = Reach(target);
            listed.Add((target, entry.Target.Parent?.Text, key.Required));
        }

        CoverageState Reach(string path) => Better(own.GetValueOrDefault(path, CoverageState.Empty), inside.GetValueOrDefault(path, CoverageState.Empty));

        var variables = Rolled(listed, states, own, direct, literals);

        var issues = new List<ValidationIssue>(RequiredIssues(mapping, template.Schema, where));
        var gated = new HashSet<string>(issues.Select(i => i.Target).OfType<string>(), StringComparer.Ordinal);
        foreach (var variable in template.Fillable)
        {
            var path = variable.Path.Text;
            var state = states[path];
            if (!variable.Required || state == CoverageState.Always || gated.Contains(path) || !Reached(variable, states))
            {
                continue;
            }

            issues.Add(state == CoverageState.Empty
                ? ValidationIssue.Warning($"{where}: template {template.Kind} requires {path}, which the mapping does not fill.", path)
                : ValidationIssue.Warning(
                    $"{where}: template {template.Kind} requires {path}, and what fills it may leave it out ($required: false or $when).", path));
        }

        return new CoverageReport(variables, issues);
    }

    /// <summary>
    /// What each variable shows: an object is only as good as what it promises, so it takes the weakest state among the
    /// variables it holds that the mapping fills or the schema requires. One required property missing makes the object
    /// holding it missing, however many of its siblings are filled, and one filled on some rows only makes it that.
    /// A property nothing fills and nothing requires promises nothing, so it is passed over rather than dragging its
    /// object down; an object promising nothing at all is left to its own entry, which is all there is to say about it.
    /// </summary>
    /// <remarks>
    /// The states the findings are judged on are <paramref name="states"/>, not these: a required property is missing
    /// because nothing writes it, never because something beside it is.
    /// </remarks>
    private static List<VariableCoverage> Rolled(
        List<(string Path, string? Holder, bool Required)> listed,
        IReadOnlyDictionary<string, CoverageState> states,
        IReadOnlyDictionary<string, CoverageState> own,
        IReadOnlySet<string> direct,
        IReadOnlyDictionary<string, (string Holder, List<string> Values)> literals)
    {
        // The variables each object holds, and then the objects read back to front, so what they hold is settled first.
        var held = new Dictionary<string, List<(string Path, bool Required)>>(StringComparer.Ordinal);
        foreach (var (path, holder, required) in listed)
        {
            if (holder is not null && states.ContainsKey(holder))
            {
                (held.TryGetValue(holder, out var siblings) ? siblings : held[holder] = []).Add((path, required));
            }
        }

        var shown = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        for (var i = listed.Count - 1; i >= 0; i--)
        {
            var path = listed[i].Path;
            var promised = held.GetValueOrDefault(path, [])
                .Where(child => states[child.Path] != CoverageState.Empty || child.Required)
                .Select(child => shown[child.Path])
                .ToList();
            shown[path] = promised.Count == 0
                ? own.GetValueOrDefault(path, CoverageState.Empty)
                : promised.Aggregate(CoverageState.Always, (worst, state) => state < worst ? state : worst);
        }

        return listed.Select(v =>
        {
            var isDirect = direct.Contains(v.Path);
            return !isDirect && literals.TryGetValue(v.Path, out var literal)
                ? new VariableCoverage(v.Path, shown[v.Path], isDirect, v.Required, literal.Holder, literal.Values)
                : new VariableCoverage(v.Path, shown[v.Path], isDirect, v.Required);
        }).ToList();
    }

    /// <summary>
    /// Where a walk through one entry's literal records what it finds: the state of each path it writes, and for each path
    /// the entry whose literal writes it with the values it gives there.
    /// </summary>
    private sealed record LiteralWalk(
        Dictionary<string, CoverageState> Written, Dictionary<string, (string Holder, List<string> Values)> Literals, string Holder);

    /// <summary>
    /// Records what a literal writes below <paramref name="path"/>, given every value it writes there (one for a literal
    /// the entry writes; one per item for the items of a list). An object writes each property it holds a value for, and a
    /// list of objects writes the properties of its items at <c>path[].name</c>. A property every holder carries is written
    /// as surely as the literal is; one only some of them carry is written for those, so no more than sometimes. A property
    /// whose name holds a dot is one step, written in brackets (<c>osdu.tags['lra.dataID']</c>).
    /// </summary>
    private static void WrittenBy(LiteralWalk walk, string path, IReadOnlyList<JsonNode> values, CoverageState state)
    {
        var objects = values.OfType<JsonObject>().ToList();
        if (objects.Count > 0)
        {
            WrittenInside(walk, path, objects, state);
        }

        var items = values.OfType<JsonArray>().SelectMany(list => list).ToList();
        if (items.Count > 0 && items.All(item => item is JsonObject))
        {
            WrittenInside(walk, path + "[]", items.Cast<JsonObject>().ToList(), state);
        }
    }

    /// <summary>Records each property <paramref name="holders"/> hold at <paramref name="holder"/>, the template path of the object they are.</summary>
    private static void WrittenInside(LiteralWalk walk, string holder, List<JsonObject> holders, CoverageState state)
    {
        var names = holders.SelectMany(holder => holder).Where(property => property.Value is not null).Select(property => property.Key)
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            var values = holders.Select(holder => holder[name]).OfType<JsonNode>().ToList();
            var reached = values.Count == holders.Count ? state : CoverageState.Sometimes;
            var path = RecordPathNotation.Append(holder, name);
            walk.Written[path] = Best(walk.Written, path, reached);

            // What the literal gives the property, so a view can show it where the property is: each value once, as written.
            if (!walk.Literals.TryGetValue(path, out var literal))
            {
                literal = (walk.Holder, []);
                walk.Literals[path] = literal;
            }

            foreach (var text in values.OfType<JsonValue>().Select(ValueText).Where(text => !literal.Values.Contains(text, StringComparer.Ordinal)))
            {
                literal.Values.Add(text);
            }

            WrittenBy(walk, path, values, reached);
        }
    }

    /// <summary>
    /// Records what the items of a list of objects write at <c>path[].name</c>: a literal item each property it holds, and an
    /// item whose properties read values each of those properties and every object on the way to one. What every item
    /// writes on every row is written as surely as the list is; what only some items write, or what an item may leave out
    /// (<c>$required: false</c> or <c>$when</c>), no more than sometimes. The values literals give a property are kept once
    /// each, in the order the items give them, so a view can show them where the property is.
    /// </summary>
    private static void WrittenByItems(LiteralWalk walk, MappingEntry list, CoverageState state)
    {
        var written = new List<string>();
        var everyRow = new Dictionary<string, int>(StringComparer.Ordinal);
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in list.Parts)
        {
            var reached = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
            if (part.Static is JsonObject item)
            {
                LiteralPaths(list.Target.Text + "[]", item, reached, values);
            }

            foreach (var property in part.Properties)
            {
                var fills = FillsEveryRow(property) ? CoverageState.Always : CoverageState.Sometimes;
                for (var path = property.Target; path is not null && !path.Equals(list.Target); path = path.Parent)
                {
                    reached[path.Text] = Better(reached.GetValueOrDefault(path.Text, CoverageState.Empty), fills);
                }

                if (property.Static is JsonObject literal)
                {
                    LiteralPaths(property.Target.Text, literal, reached, values);
                }
                else if (property.Static is JsonValue value)
                {
                    Add(values, property.Target.Text, ValueText(value));
                }
            }

            foreach (var (path, reach) in reached)
            {
                if (!everyRow.ContainsKey(path))
                {
                    written.Add(path);
                    everyRow[path] = 0;
                }

                everyRow[path] += reach == CoverageState.Always ? 1 : 0;
            }
        }

        foreach (var path in written)
        {
            var reached = everyRow[path] == list.Parts.Count ? state : CoverageState.Sometimes;
            walk.Written[path] = Best(walk.Written, path, reached);
            if (!walk.Literals.TryGetValue(path, out var literal))
            {
                literal = (walk.Holder, []);
                walk.Literals[path] = literal;
            }

            foreach (var text in values.GetValueOrDefault(path, []).Where(text => !literal.Values.Contains(text, StringComparer.Ordinal)))
            {
                literal.Values.Add(text);
            }
        }
    }

    /// <summary>Every property a literal object at <paramref name="holder"/> holds a value for, written on every row, with the values it gives them.</summary>
    private static void LiteralPaths(string holder, JsonObject literal, Dictionary<string, CoverageState> reached, Dictionary<string, List<string>> values)
    {
        foreach (var (name, child) in literal)
        {
            if (child is null)
            {
                continue;
            }

            var path = RecordPathNotation.Append(holder, name);
            reached[path] = CoverageState.Always;
            switch (child)
            {
                case JsonObject nested:
                    LiteralPaths(path, nested, reached, values);
                    break;
                case JsonValue value:
                    Add(values, path, ValueText(value));
                    break;
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> values, string path, string text)
    {
        if (!values.TryGetValue(path, out var list))
        {
            list = [];
            values[path] = list;
        }

        if (!list.Contains(text, StringComparer.Ordinal))
        {
            list.Add(text);
        }
    }

    /// <summary>
    /// Records every variable of the template below <paramref name="path"/> as written, on some rows, by the entry that
    /// writes the object or list at <paramref name="path"/> whole: what that value holds is known only when it is rendered.
    /// </summary>
    private static void WrittenWhole(LiteralWalk walk, OsduTemplate template, string path)
    {
        foreach (var variable in template.Fillable)
        {
            var below = variable.Path.Text;
            if (below == path || !TemplatePath.IsWithin(below, path))
            {
                continue;
            }

            walk.Written[below] = Best(walk.Written, below, CoverageState.Sometimes);
            walk.Literals.TryAdd(below, (walk.Holder, []));
        }
    }

    /// <summary>A literal value as the document writes it: text as it is, a number or a boolean in its JSON form.</summary>
    private static string ValueText(JsonValue value)
        => value.TryGetValue<string>(out var text) ? text : value.ToJsonString();

    /// <summary>
    /// Whether a required variable is reachable at all: every object on the way to it is filled. A property required inside
    /// an object the mapping never fills is not missing, because the record holds no such object.
    /// </summary>
    private static bool Reached(TemplateVariable variable, IReadOnlyDictionary<string, CoverageState> states)
    {
        for (var holder = variable.Path.Parent; holder is not null; holder = holder.Parent)
        {
            if (states.TryGetValue(holder.Text, out var state) && state == CoverageState.Empty)
            {
                return false;
            }
        }

        return true;
    }

    private static CoverageState Best(Dictionary<string, CoverageState> states, string path, CoverageState state)
        => Better(states.GetValueOrDefault(path, CoverageState.Empty), state);

    /// <summary>The stronger of two states: what reaches every row over what reaches some, and either over nothing.</summary>
    private static CoverageState Better(CoverageState left, CoverageState right) => left > right ? left : right;
}
