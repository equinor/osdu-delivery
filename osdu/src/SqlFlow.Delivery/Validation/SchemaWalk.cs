using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// One way a value breaks what its schema says of it, or one part of it no rule could be checked on.
/// </summary>
/// <param name="At">
/// Where, by the property path every value of the same property shares: <c>data.VerticalMeasurements[].VerticalCRSID</c>
/// in a record, or the variable path (<c>osdu.data.Items[].Name</c>) for the value of one variable.
/// </param>
/// <param name="Path">Exactly where: <c>data.VerticalMeasurements[2].VerticalCRSID</c>, or empty for the value checked itself.</param>
/// <param name="Rule">The JSON Schema keyword, <c>relationship</c>, <c>reference</c>, or for a part not checked, why.</param>
/// <param name="Message">What is wrong, naming the value.</param>
/// <param name="Value">The value as text, clipped.</param>
public sealed record SchemaFinding(string At, string Path, string Rule, string Message, string Value);

/// <summary>An OSDU id a value names at a property the schema marks as a relationship.</summary>
/// <param name="Id">The id as written, without surrounding space.</param>
/// <param name="EntityType">The entity type it names a record of (<c>master-data--Wellbore</c>).</param>
/// <param name="At">The property path every value of the property shares.</param>
/// <param name="Path">Where the id was found first.</param>
public sealed record FoundReference(string Id, string EntityType, string At, string Path);

/// <summary>How far a walk goes and what it keeps: one set for a value of one variable, another for a whole record.</summary>
internal sealed record WalkLimits
{
    /// <summary>
    /// The limits a value of one variable is checked within (Check values, a render's values): the first problems are what
    /// is needed, and a check reads the values of every row of a scope, so a walk stops at its first ten problems.
    /// </summary>
    public static WalkLimits ForValue { get; } = new()
    {
        MaxListed = TemplateValueRules.MaxProblems,
        StopWhenListed = true,
        MaxItems = TemplateValueRules.MaxItems,
        MaxDepth = TemplateValueRules.MaxDepth,
        PrefixMessages = true,
        TimeoutIsProblem = true,
    };

    public int MaxListed { get; init; }

    /// <summary>Whether the walk stops once it has listed <see cref="MaxListed"/> problems, instead of counting them all.</summary>
    public bool StopWhenListed { get; init; }

    public int MaxUnverifiedListed { get; init; }

    public int MaxItems { get; init; }

    public int MaxDepth { get; init; }

    public long MaxValues { get; init; } = long.MaxValue;

    public TimeSpan? Budget { get; init; }

    public int MaxReferences { get; init; }

    /// <summary>Whether a message starts with where inside the value it is (<c>at [1].Name: </c>).</summary>
    public bool PrefixMessages { get; init; }

    /// <summary>
    /// Whether a pattern match that runs out of time is a problem, as Check values has always shown it, rather than a part
    /// not checked.
    /// </summary>
    public bool TimeoutIsProblem { get; init; }

    /// <summary>The property paths (<c>data.Datasets</c>) a route fills when it sends the record, left unjudged.</summary>
    public IReadOnlySet<string> RouteFilled { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The property paths whose value is read as absent: left unjudged, and missing where the schema requires them.</summary>
    public IReadOnlySet<string> Absent { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// The walk of a value and its rules side by side (<see cref="RuleNode"/>): every rule JSON Schema draft-07 states that the
/// rules hold, applied to the value and to everything inside it. What it finds goes to its <see cref="Sink"/>; what it could
/// not check is noted there as well, when the limits ask for it, so a value no rule was applied to never reads as one that
/// met them.
/// </summary>
/// <remarks>
/// A <c>oneOf</c> is read as an <c>anyOf</c>: a value matching one of its forms passes, since OSDU's forms overlap in ways a
/// storage write never objects to. A format the rules do not know is not asserted, as JSON Schema lets a validator do. The
/// walk is bounded by its limits: how deep, how many items of a list, how many values, how long.
/// </remarks>
internal sealed class SchemaWalk
{
    /// <summary>How often, in values, the walk looks at the clock.</summary>
    private const int ClockEvery = 256;

    private readonly WalkLimits _limits;
    private readonly Stopwatch? _clock;

    private SchemaWalk(WalkLimits limits, Sink sink, Stopwatch? clock)
    {
        _limits = limits;
        Found = sink;
        _clock = clock;
    }

    /// <summary>What the walk found.</summary>
    public Sink Found { get; }

    /// <summary>Walks <paramref name="value"/> against <paramref name="rules"/>, starting at <paramref name="place"/>.</summary>
    public static Sink Run(JsonNode? value, RuleNode rules, Place place, WalkLimits limits)
    {
        var walk = new SchemaWalk(limits, new Sink(limits), limits.Budget is null ? null : Stopwatch.StartNew());
        walk.Node(value, rules, place, 0);
        return walk.Found;
    }

    /// <summary>
    /// Where a walk is: the path every value of the property shares, and the position inside what is checked. A name holding a
    /// dot, a bracket or a space is one step in brackets (<c>tags['lra.dataID']</c>), as a template path names it.
    /// </summary>
    internal readonly record struct Place(string At, string Path)
    {
        /// <summary>The place of property <paramref name="name"/> of an object here; an item of a list names its property with [].</summary>
        public Place Property(string name, bool inItem)
            => new(RecordPathNotation.Append(inItem ? At + "[]" : At, name), RecordPathNotation.Append(Path, name));

        public Place Item(int index) => this with { Path = string.Create(CultureInfo.InvariantCulture, $"{Path}[{index}]") };
    }

    /// <summary>What a walk found: the problems and the parts not checked (listed up to the limits, counted exactly), the ids at relationships, and how much it checked.</summary>
    internal sealed class Sink(WalkLimits limits)
    {
        private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);

        public List<SchemaFinding> Problems { get; } = [];

        public long ProblemCount { get; private set; }

        public List<SchemaFinding> Unverified { get; } = [];

        public long UnverifiedCount { get; private set; }

        public List<FoundReference> References { get; } = [];

        /// <summary>Whether ids past <see cref="WalkLimits.MaxReferences"/> were found and not kept.</summary>
        public bool ReferencesCut { get; private set; }

        /// <summary>How many rules were applied.</summary>
        public long Rules { get; set; }

        /// <summary>How many values were walked.</summary>
        public long Values { get; set; }

        /// <summary>Why the walk stopped before the end of the value, or null when it reached the end.</summary>
        public string? StoppedBecause { get; set; }

        public bool Stopped => StoppedBecause is not null || (limits.StopWhenListed && Problems.Count >= limits.MaxListed);

        public void Problem(SchemaFinding finding)
        {
            ProblemCount++;
            if (Problems.Count < limits.MaxListed)
            {
                Problems.Add(finding);
            }
        }

        public void NotChecked(SchemaFinding finding)
        {
            UnverifiedCount++;
            if (Unverified.Count < limits.MaxUnverifiedListed)
            {
                Unverified.Add(finding);
            }
        }

        public void Reference(FoundReference reference)
        {
            if (limits.MaxReferences <= 0 || _referenced.Contains(reference.Id))
            {
                return;
            }

            if (_referenced.Count >= limits.MaxReferences)
            {
                ReferencesCut = true;
                return;
            }

            _referenced.Add(reference.Id);
            References.Add(reference);
        }

        /// <summary>Takes in what a trial of a form found that the walk keeps when the form is the one matched.</summary>
        public void Absorb(Sink trial)
        {
            foreach (var finding in trial.Unverified)
            {
                NotChecked(finding);
            }

            UnverifiedCount += trial.UnverifiedCount - trial.Unverified.Count;
            foreach (var reference in trial.References)
            {
                Reference(reference);
            }

            ReferencesCut |= trial.ReferencesCut;
            Rules += trial.Rules;
            Values += trial.Values;
            StoppedBecause ??= trial.StoppedBecause;
        }
    }

    private bool KeepsUnverified => _limits.MaxUnverifiedListed > 0;

    private void Node(JsonNode? value, RuleNode rules, Place place, int depth, bool inItem = false)
    {
        if (Found.Stopped)
        {
            return;
        }

        if (depth > _limits.MaxDepth)
        {
            NotChecked(place, "depth", string.Create(CultureInfo.InvariantCulture, $"the value is nested more than {_limits.MaxDepth} levels deep, so what is inside it is not checked"), value);
            return;
        }

        if (++Found.Values % ClockEvery == 0 || Found.Values > _limits.MaxValues)
        {
            if (OverBudget() is { } why)
            {
                Found.StoppedBecause = why;
                NotChecked(place, "budget", why, value);
                return;
            }
        }

        if (rules.Unchecked is { } cannotCheck)
        {
            NotChecked(place, "schema", cannotCheck, value);
            return;
        }

        foreach (var keyword in rules.UncheckedKeywords)
        {
            NotChecked(place, keyword, $"the schema's '{keyword}' here is not checked", value);
        }

        if (value is null)
        {
            Null(rules, place);
            return;
        }

        if (!Forms(value, rules, place, depth, inItem))
        {
            return;
        }

        if (rules.Types.Count > 0)
        {
            Found.Rules++;
            if (!rules.Types.Any(type => IsType(value, type)))
            {
                Problem(place, "type", $"{Describe(value)} where the schema takes {string.Join(" or ", rules.Types.Select(Article))}", value);
                return;
            }
        }

        foreach (var constant in rules.Constants)
        {
            Found.Rules++;
            if (!JsonNode.DeepEquals(constant, value))
            {
                Problem(place, "const", $"{Describe(value)} is not {Quote(constant)}, the one value the schema allows", value);
            }
        }

        foreach (var allowed in rules.Enumerations)
        {
            Found.Rules++;
            if (!allowed.Any(option => JsonNode.DeepEquals(option, value)))
            {
                var listed = string.Join(", ", allowed.Take(8).Select(Quote))
                    + (allowed.Count > 8 ? string.Create(CultureInfo.InvariantCulture, $" and {allowed.Count - 8} more") : string.Empty);
                Problem(place, "enum", string.Create(CultureInfo.InvariantCulture, $"{Describe(value)} is not one of the {allowed.Count} values the schema allows ({listed})"), value);
            }
        }

        switch (value)
        {
            case JsonObject obj:
                Object(obj, rules, place, depth, inItem);
                break;
            case JsonArray list:
                List(list, rules, place, depth);
                break;
            case JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String:
                Text(scalar.GetValue<string>(), rules, place, value);
                break;
            case JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number:
                Number(scalar, rules, place);
                break;
        }
    }

    /// <summary>A <c>null</c> breaks a schema that names types without naming <c>null</c>; one that names none allows it.</summary>
    private void Null(RuleNode rules, Place place)
    {
        if (!rules.TypeDeclared)
        {
            return;
        }

        Found.Rules++;
        if (!rules.AllowsNull && rules.Types.Count > 0)
        {
            Problem(place, "type", $"null where the schema takes {string.Join(" or ", rules.Types.Select(Article))}", null);
        }
    }

    /// <summary>
    /// Whether the value matches one of the forms each <c>oneOf</c> or <c>anyOf</c> allows; true when the node lists none. A
    /// value matching none is a problem of its own, naming what the first form found wrong with it. What the matching form's
    /// trial noted as not checked, and the ids it found, are the walk's.
    /// </summary>
    private bool Forms(JsonNode value, RuleNode rules, Place place, int depth, bool inItem)
    {
        foreach (var forms in rules.Choices)
        {
            if (forms.Count == 0)
            {
                continue;
            }

            Found.Rules++;
            Sink? first = null;
            Sink? matched = null;
            foreach (var form in forms)
            {
                // A trial needs one problem to fail the form, so it stops there.
                var trialLimits = _limits with { MaxListed = 1, StopWhenListed = true, MaxValues = Math.Max(0, _limits.MaxValues - Found.Values) };
                var trial = new SchemaWalk(trialLimits, new Sink(trialLimits), _clock);
                trial.Node(value, form, place, depth + 1, inItem);
                if (trial.Found.ProblemCount == 0)
                {
                    matched = trial.Found;
                    break;
                }

                first ??= trial.Found;
                Found.Values += trial.Found.Values;
            }

            if (matched is null)
            {
                var why = first is { Problems.Count: > 0 } ? $": {first.Problems[0].Message}" : string.Empty;
                Problem(place, "anyOf", string.Create(CultureInfo.InvariantCulture, $"{Describe(value)} matches none of the {forms.Count} forms the schema allows{why}"), value);
                return false;
            }

            Found.Absorb(matched);
        }

        return true;
    }

    private void Object(JsonObject obj, RuleNode rules, Place place, int depth, bool inItem)
    {
        foreach (var name in rules.Required)
        {
            Found.Rules++;
            var at = place.Property(name, inItem);
            if ((!obj.ContainsKey(name) || _limits.Absent.Contains(at.At)) && !_limits.RouteFilled.Contains(at.At))
            {
                // A whole record checked from its root is named as the record; anything else is the value at its place.
                var subject = !_limits.PrefixMessages && place.Path.Length == 0 ? "the record" : "the value";
                Problem(at, "required", $"{Prefix(place)}{subject} has no {name}, which the schema requires", obj);
            }
        }

        foreach (var (name, child) in obj)
        {
            if (Found.Stopped)
            {
                return;
            }

            var at = place.Property(name, inItem);
            if (_limits.RouteFilled.Contains(at.At) || _limits.Absent.Contains(at.At))
            {
                continue;
            }

            if (rules.Properties.TryGetValue(name, out var declared))
            {
                Node(child, declared, at, depth + 1);
            }
            else if (rules.Additional is { } free)
            {
                Node(child, free, at, depth + 1);
            }
            else if (rules.AdditionalForbidden)
            {
                Found.Rules++;
                Problem(at, "additionalProperties", $"{Prefix(place)}the value has {name}, which the schema does not describe and allows no other property", child);
            }
        }
    }

    private void List(JsonArray list, RuleNode rules, Place place, int depth)
    {
        if (rules.MinItems is { } fewest)
        {
            Found.Rules++;
            if (list.Count < fewest)
            {
                Problem(place, "minItems", string.Create(CultureInfo.InvariantCulture, $"{Prefix(place)}the list holds {list.Count} item(s), fewer than the {fewest} the schema requires"), list);
            }
        }

        if (rules.MaxItems is { } most)
        {
            Found.Rules++;
            if (list.Count > most)
            {
                Problem(place, "maxItems", string.Create(CultureInfo.InvariantCulture, $"{Prefix(place)}the list holds {list.Count} items, more than the {most} the schema allows"), list);
            }
        }

        if (rules.UniqueItems)
        {
            Found.Rules++;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.Take(_limits.MaxItems))
            {
                if (!seen.Add(CanonicalJson.ToString(item)))
                {
                    Problem(place, "uniqueItems", $"{Prefix(place)}{Quote(item)} is in the list more than once, and the schema takes each item once", item);
                    break;
                }
            }
        }

        if (rules.ItemsUnchecked is { } why && list.Count > 0)
        {
            NotChecked(place, "items", why, list);
        }

        if (rules.Items is not { } items)
        {
            return;
        }

        var index = 0;
        foreach (var item in list.Take(_limits.MaxItems))
        {
            if (Found.Stopped)
            {
                return;
            }

            Node(item, items, place.Item(index), depth + 1, inItem: true);
            index++;
        }

        if (list.Count > _limits.MaxItems)
        {
            NotChecked(place, "items", string.Create(CultureInfo.InvariantCulture, $"the list holds {list.Count} items, and items past the first {_limits.MaxItems} are not checked"), list);
        }
    }

    private void Text(string text, RuleNode rules, Place place, JsonNode value)
    {
        var related = false;
        if (rules.Relationships.Count > 0)
        {
            related = true;
            Found.Rules++;
            var trimmed = text.Trim();
            if (IdValues.EntityType(trimmed) is not { } entityType)
            {
                Problem(place, "relationship", $"{Prefix(place)}{Quote(value)} is not an OSDU id, and the schema points the property to {string.Join(" or ", rules.Relationships)}", value);
                return;
            }

            if (!IdValues.Allows(rules.Relationships, entityType))
            {
                Problem(place, "relationship", $"{Prefix(place)}{Quote(value)} is the id of a {entityType} record, and the schema points the property to {string.Join(" or ", rules.Relationships)}", value);
                return;
            }

            Found.Reference(new FoundReference(trimmed, entityType, place.At, place.Path));
        }

        foreach (var pattern in rules.Patterns)
        {
            if (pattern.Regex is not { } regex)
            {
                NotChecked(place, "pattern", $"the pattern {pattern.Text} is read by neither ECMAScript nor .NET, so the value is not checked against it", value);
                continue;
            }

            Found.Rules++;
            try
            {
                if (!regex.IsMatch(text))
                {
                    Problem(place, "pattern", related
                        ? $"{Prefix(place)}{Quote(value)} does not match the pattern of the ids the schema takes, {pattern.Text}"
                        : $"{Prefix(place)}{Quote(value)} does not match the pattern the schema gives, {pattern.Text}", value);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                var message = $"{Prefix(place)}{Quote(value)} could not be checked against the pattern {pattern.Text} within the time a check allows";
                if (_limits.TimeoutIsProblem)
                {
                    Problem(place, "pattern", message, value);
                }
                else
                {
                    NotChecked(place, "pattern", message, value);
                }
            }
        }

        if (rules.MinLength is not null || rules.MaxLength is not null)
        {
            var length = text.EnumerateRunes().Count();
            if (rules.MinLength is { } shortest)
            {
                Found.Rules++;
                if (length < shortest)
                {
                    Problem(place, "minLength", string.Create(CultureInfo.InvariantCulture, $"{Prefix(place)}{Quote(value)} is {length} character(s) long, shorter than the {shortest} the schema requires"), value);
                }
            }

            if (rules.MaxLength is { } longest)
            {
                Found.Rules++;
                if (length > longest)
                {
                    Problem(place, "maxLength", string.Create(CultureInfo.InvariantCulture, $"{Prefix(place)}the text is {length} characters long, longer than the {longest} the schema allows"), value);
                }
            }
        }

        foreach (var format in rules.Formats)
        {
            if (!TemplateValueRules.AssertsFormat(format))
            {
                continue;
            }

            Found.Rules++;
            if (TemplateValueRules.FormatProblem(text, format) is { } wrong)
            {
                Problem(place, "format", $"{Prefix(place)}{Quote(value)} {wrong}", value);
            }
        }
    }

    private void Number(JsonValue scalar, RuleNode rules, Place place)
    {
        var text = scalar.ToJsonString();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return;
        }

        if (rules.Minimum is { } minimum)
        {
            Found.Rules++;
            if (number < minimum)
            {
                Problem(place, "minimum", $"{Prefix(place)}{text} is below the minimum of {Format(minimum)} the schema gives", scalar);
            }
        }

        if (rules.Maximum is { } maximum)
        {
            Found.Rules++;
            if (number > maximum)
            {
                Problem(place, "maximum", $"{Prefix(place)}{text} is above the maximum of {Format(maximum)} the schema gives", scalar);
            }
        }

        if (rules.ExclusiveMinimum is { } above)
        {
            Found.Rules++;
            if (number <= above)
            {
                Problem(place, "exclusiveMinimum", $"{Prefix(place)}{text} is not above {Format(above)}, which the schema requires", scalar);
            }
        }

        if (rules.ExclusiveMaximum is { } below)
        {
            Found.Rules++;
            if (number >= below)
            {
                Problem(place, "exclusiveMaximum", $"{Prefix(place)}{text} is not below {Format(below)}, which the schema requires", scalar);
            }
        }

        if (rules.MultiplesOf.Count > 0)
        {
            var exact = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var precise) ? precise : (decimal?)null;
            foreach (var step in rules.MultiplesOf)
            {
                Found.Rules++;
                if (exact is { } value && decimal.Remainder(value, step.Step) != 0)
                {
                    Problem(place, "multipleOf", $"{Prefix(place)}{text} is not a multiple of {step.Text}, which the schema requires", scalar);
                }
            }
        }

        if (rules.Types.Contains("integer"))
        {
            foreach (var format in rules.Formats)
            {
                var (low, high) = format switch
                {
                    "int32" => (int.MinValue, (double)int.MaxValue),
                    "int64" => ((double)long.MinValue, (double)long.MaxValue),
                    _ => (double.NegativeInfinity, double.PositiveInfinity),
                };
                if (double.IsInfinity(low))
                {
                    continue;
                }

                Found.Rules++;
                if (number < low || number > high)
                {
                    Problem(place, "format", $"{Prefix(place)}{text} is outside the range of an {format} integer, which the schema takes", scalar);
                }
            }
        }
    }

    /// <summary>Why the walk must stop now, or null while it is within its limits.</summary>
    private string? OverBudget()
    {
        if (Found.Values > _limits.MaxValues)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the check stopped after {_limits.MaxValues:N0} values, the most one check walks, so the rest of the value is not checked");
        }

        if (_clock is not null && _limits.Budget is { } budget && _clock.Elapsed > budget)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the check stopped after {budget.TotalSeconds:0.#} second(s), the longest one check runs, so the rest of the value is not checked");
        }

        return null;
    }

    private string Prefix(Place place) => !_limits.PrefixMessages || place.Path.Length == 0 ? string.Empty : $"at {place.Path}: ";

    private void Problem(Place place, string rule, string message, JsonNode? value)
        => Found.Problem(new SchemaFinding(place.At, place.Path, rule, message, Clip(value)));

    private void NotChecked(Place place, string rule, string message, JsonNode? value)
    {
        if (KeepsUnverified)
        {
            Found.NotChecked(new SchemaFinding(place.At, place.Path, rule, message, Clip(value)));
        }
    }

    internal static bool IsType(JsonNode value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && IsWhole(v.ToJsonString()),
        _ => true,
    };

    private static bool IsWhole(string number)
        => decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            ? decimal.Truncate(exact) == exact
            : double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide) && double.IsFinite(wide) && Math.Floor(wide) == wide;

    private static string Article(string type) => type switch
    {
        "object" => "an object",
        "array" => "a list",
        "integer" => "an integer",
        "string" => "text",
        "boolean" => "true or false",
        _ => "a " + type,
    };

    /// <summary>A value as a message names it: its kind, and a scalar's text.</summary>
    private static string Describe(JsonNode value) => value switch
    {
        JsonObject => "an object",
        JsonArray list => string.Create(CultureInfo.InvariantCulture, $"a list of {list.Count}"),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => $"the text {Quote(value)}",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => $"the number {v.ToJsonString()}",
        _ => value.ToJsonString(),
    };

    /// <summary>A value quoted in a message: text in single quotes, anything else as JSON, clipped.</summary>
    private static string Quote(JsonNode? value)
        => value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? $"'{Clip(value)}'" : Clip(value);

    /// <summary>A value as text, no longer than <see cref="TemplateValueRules.MaxQuoted"/> characters.</summary>
    internal static string Clip(JsonNode? value)
    {
        var text = value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value is null ? "null" : CanonicalJson.ToString(value);
        return text.Length <= TemplateValueRules.MaxQuoted ? text : text[..TemplateValueRules.MaxQuoted] + "...";
    }

    private static string Format(double number) => number.ToString("R", CultureInfo.InvariantCulture);
}
