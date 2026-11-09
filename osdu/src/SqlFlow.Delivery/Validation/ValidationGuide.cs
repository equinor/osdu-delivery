using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// What a schema expects of the value at one place of a record (osdu/docs/reference/concepts/explorer.md, Guidance on
/// each problem): what the schema says of it in words, the rules it holds the value to, examples of it, and one line
/// saying what a value there is.
/// </summary>
public sealed record ValueExpectation
{
    /// <summary>The place, as a finding names it (<c>data.VerticalMeasurements[].VerticalCRSID</c>), with <c>[]</c> at the end for each item of the list there.</summary>
    public required string At { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    /// <summary>One line saying what a value here is: <c>text matching ^NO , of at most 64 characters</c>.</summary>
    public required string Summary { get; init; }

    /// <summary>The JSON types a value may be, <c>null</c> among them when the schema allows it; empty when it names none.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>Whether the object holding the value requires it; false for an item of a list and for the record itself.</summary>
    public bool Required { get; init; }

    public IReadOnlyList<string> Patterns { get; init; } = [];

    /// <summary>The values allowed (a <c>const</c>, else every <c>enum</c> agrees on them), at most <see cref="ValidationGuide.MaxAllowed"/>, text unquoted.</summary>
    public IReadOnlyList<string> Allowed { get; init; } = [];

    /// <summary>How many values are allowed, listed or not; zero when the schema lists none.</summary>
    public int AllowedCount { get; init; }

    public IReadOnlyList<string> Formats { get; init; } = [];

    public long? MinLength { get; init; }

    public long? MaxLength { get; init; }

    public long? MinItems { get; init; }

    public long? MaxItems { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? ExclusiveMinimum { get; init; }

    public double? ExclusiveMaximum { get; init; }

    public IReadOnlyList<string> MultipleOf { get; init; } = [];

    public bool UniqueItems { get; init; }

    /// <summary>The entity types an OSDU id written here may be of (<c>x-osdu-relationship</c>).</summary>
    public IReadOnlyList<string> EntityTypes { get; init; } = [];

    /// <summary>For an object: the properties the schema names, at most <see cref="ValidationGuide.MaxProperties"/>.</summary>
    public IReadOnlyList<string> Properties { get; init; } = [];

    /// <summary>How many properties the schema names, listed or not.</summary>
    public int PropertyCount { get; init; }

    public IReadOnlyList<string> RequiredProperties { get; init; } = [];

    /// <summary>Whether an object here may hold no property the schema does not name.</summary>
    public bool OnlyNamedProperties { get; init; }

    /// <summary>For a list: what each item is, in words.</summary>
    public string? Items { get; init; }

    /// <summary>How many forms a <c>oneOf</c> or <c>anyOf</c> here allows; zero when there is none.</summary>
    public int Forms { get; init; }

    /// <summary>The examples the schema gives (<c>example</c>, <c>examples</c>), at most <see cref="SchemaDocs.MaxExamples"/>.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>The value the OSDU data definitions' example record holds here, the first item of each list taken; null when there is none.</summary>
    public string? OsduExample { get; init; }
}

/// <summary>What one finding comes to for the person fixing it.</summary>
/// <param name="Path">The finding's path, as the verdict lists it.</param>
/// <param name="Rule">The finding's rule.</param>
/// <param name="Expected">The key of what the schema expects there (<see cref="ValidationGuidance.Expectations"/>), or null when the schema describes nothing there.</param>
/// <param name="Found">The value found there, in words: <c>null</c>, <c>absent</c>, <c>'Wellbore A-1'</c>, <c>a list of 3 items</c>.</param>
/// <param name="Advice">How to make the value meet the schema, or null when there is nothing to say beyond the message.</param>
public sealed record FindingGuide(string Path, string Rule, string? Expected, string Found, string? Advice);

/// <summary>The example record of the OSDU data definitions the guidance quotes.</summary>
public sealed record GuidanceExample(string Release, string Path, Uri WebUrl);

/// <summary>
/// What a verdict comes to for the person fixing the record: for each finding, the value found, what the schema expects
/// there and how to meet it, in the order the verdict lists the findings.
/// </summary>
public sealed record ValidationGuidance
{
    public static ValidationGuidance None { get; } = new();

    /// <summary>What the schema expects, by the place a finding names (<see cref="FindingGuide.Expected"/>).</summary>
    public IReadOnlyDictionary<string, ValueExpectation> Expectations { get; init; } = new Dictionary<string, ValueExpectation>(StringComparer.Ordinal);

    /// <summary>One guide per problem the verdict lists, in its order.</summary>
    public IReadOnlyList<FindingGuide> Problems { get; init; } = [];

    /// <summary>One guide per part not checked the verdict lists, in its order.</summary>
    public IReadOnlyList<FindingGuide> Unverified { get; init; } = [];

    /// <summary>The OSDU data definitions' example record the expectations quote, or null when none was read.</summary>
    public GuidanceExample? Example { get; init; }

    /// <summary>Why no example of the data definitions is quoted, when one was looked for and not had.</summary>
    public string? ExampleNote { get; init; }
}

/// <summary>
/// Turns the findings of a check into guidance a person can act on (osdu/docs/reference/concepts/explorer.md, Guidance
/// on each problem): the value found, what the schema expects there in its own words (its title, description, rules and
/// examples, and the value the OSDU data definitions' example record holds there), and how to make the value meet it.
/// The guidance is worked out from the schema the check read and the record it checked; a verdict stores none of it, so
/// a template version, which never changes, gives the same guidance whenever it is asked.
/// </summary>
public static class ValidationGuide
{
    /// <summary>The most allowed values an expectation lists.</summary>
    public const int MaxAllowed = 25;

    /// <summary>The most property names an expectation lists.</summary>
    public const int MaxProperties = 40;

    /// <summary>The longest found value quoted.</summary>
    public const int MaxFound = 160;

    /// <summary>The longest value of the data definitions' example quoted.</summary>
    public const int MaxOsduExample = 300;

    /// <summary>The most values or names one line of advice lists.</summary>
    private const int ListedInAdvice = 10;

    /// <summary>The longest example one line of advice quotes.</summary>
    private const int ExampleInAdvice = 120;

    /// <summary>How many levels of lists in lists an item is looked for through.</summary>
    private const int MaxNestedLists = 8;

    private static readonly string[] UncheckedKeywords =
        ["not", "if", "contains", "patternProperties", "propertyNames", "dependencies", "additionalItems", "minProperties", "maxProperties"];

    /// <summary>The guidance of <paramref name="verdict"/>, reached by checking <paramref name="record"/> against <paramref name="rules"/>.</summary>
    /// <param name="rules">The rules the record was checked against.</param>
    /// <param name="verdict">What the check came to.</param>
    /// <param name="record">The record checked, to say what was found; null when it is not at hand, and the findings' own values are said.</param>
    /// <param name="example">The data definitions' example record of the kind, quoted where it holds a value; null when none was read.</param>
    /// <param name="exampleNote">Why no example was read, when one was looked for.</param>
    public static ValidationGuidance Of(SchemaRules rules, ValidationVerdict verdict, JsonNode? record, OfficialExample? example = null, string? exampleNote = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(verdict);
        var expectations = new Dictionary<string, ValueExpectation>(StringComparer.Ordinal);
        var problems = verdict.Problems.Select(finding => Guide(rules, finding, record, example, problem: true, expectations)).ToList();
        var unverified = verdict.Unverified.Select(finding => Guide(rules, finding, record, example, problem: false, expectations)).ToList();
        return new ValidationGuidance
        {
            Expectations = expectations,
            Problems = problems,
            Unverified = unverified,
            Example = example is null ? null : new GuidanceExample(example.Release, example.Path, example.WebUrl),
            ExampleNote = exampleNote,
        };
    }

    /// <summary>The guide of one problem, and what the schema expects where it is: a list's example of a rule broken.</summary>
    public static (FindingGuide Guide, ValueExpectation? Expected) For(SchemaRules rules, SchemaFinding finding, JsonNode? record, OfficialExample? example = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(finding);
        var expectations = new Dictionary<string, ValueExpectation>(StringComparer.Ordinal);
        var guide = Guide(rules, finding, record, example, problem: true, expectations);
        return (guide, guide.Expected is { } key ? expectations.GetValueOrDefault(key) : null);
    }

    /// <summary>
    /// What the schema of <paramref name="rules"/> expects at <paramref name="at"/>: a place as a finding names it
    /// (<c>data.Curves[].CurveID</c>), with <c>[]</c> at the end for each item of the list there, and the empty place for the
    /// record itself. Null when the schema describes nothing there.
    /// </summary>
    public static ValueExpectation? Expect(SchemaRules rules, string at, OfficialExample? example = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(at);
        var item = at.EndsWith("[]", StringComparison.Ordinal);
        var body = item ? at[..^2] : at;
        RuleNode node;
        SchemaDocs docs;
        var required = false;
        if (body.Length == 0)
        {
            node = rules.Root;
            docs = node.Docs;
        }
        else
        {
            if (PlaceSteps(body) is not { } steps)
            {
                return null;
            }

            var current = rules.Root;
            var holder = current;
            RuleNode? last = null;
            var name = string.Empty;
            foreach (var (stepName, inItem) in steps)
            {
                name = stepName;
                holder = current;
                last = Declared(current, name);
                if (last is null)
                {
                    return null;
                }

                current = inItem ? Element(last) : last;
            }

            node = last!;
            docs = holder.PropertyDocs(name) ?? node.Docs;
            required = holder.Required.Contains(name, StringComparer.Ordinal);
        }

        if (item)
        {
            node = Element(node);
            docs = node.Docs;
            required = false;
        }

        return Describe(node, docs, required, at, example);
    }

    private static FindingGuide Guide(
        SchemaRules rules, SchemaFinding finding, JsonNode? record, OfficialExample? example, bool problem, Dictionary<string, ValueExpectation> expectations)
    {
        var key = KeyOf(finding);
        var expected = Remember(rules, key, example, expectations);
        var holder = problem && finding.Rule == "additionalProperties" ? Remember(rules, HolderOf(finding.At), example, expectations) : null;
        var (found, node, known) = Found(finding, record);
        var advice = problem ? Advise(finding, expected, holder, node, known) : NotCheckedAdvice(finding);
        return new FindingGuide(
            finding.Path,
            finding.Rule,
            expected is null ? null : key,
            HeaderRedaction.RedactMessage(found),
            advice is null ? null : HeaderRedaction.RedactMessage(advice));
    }

    private static ValueExpectation? Remember(SchemaRules rules, string key, OfficialExample? example, Dictionary<string, ValueExpectation> expectations)
    {
        if (expectations.TryGetValue(key, out var known))
        {
            return known;
        }

        var expected = Expect(rules, key, example);
        if (expected is not null)
        {
            expectations[key] = expected;
        }

        return expected;
    }

    /// <summary>The place a finding is about: its property's, or each item's of the list there when its path ends at an item.</summary>
    private static string KeyOf(SchemaFinding finding)
        => finding.Path.EndsWith(']') && !finding.At.EndsWith("[]", StringComparison.Ordinal) ? finding.At + "[]" : finding.At;

    /// <summary>The place of the object holding the property at <paramref name="at"/>; the record's for a property of its own.</summary>
    private static string HolderOf(string at) => RecordPathNotation.HolderOf(at);

    /// <summary>
    /// The properties a place as a finding names it walks through (<c>data.Curves[].CurveID</c>, <c>tags['lra.dataID']</c>),
    /// each with whether the place goes on into its items; null for what is not such a place.
    /// </summary>
    private static List<(string Name, bool InItem)>? PlaceSteps(string at)
    {
        if (!RecordPathNotation.TryParse(at, out var steps, out _))
        {
            return null;
        }

        var places = new List<(string Name, bool InItem)>(steps.Count);
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case RecordPathStepKind.Property:
                    places.Add((step.Name!, false));
                    break;
                case RecordPathStepKind.Items when places.Count > 0 && !places[^1].InItem:
                    places[^1] = (places[^1].Name, true);
                    break;
                default:
                    return null;
            }
        }

        return places;
    }

    /// <summary>The rules of a property an object names, among its own or among the forms a choice allows.</summary>
    private static RuleNode? Declared(RuleNode node, string name)
        => node.Property(name)
            ?? node.Choices.SelectMany(forms => forms).Select(form => form.Properties.GetValueOrDefault(name)).FirstOrDefault(found => found is not null);

    /// <summary>The rules each item of a list here is held to, through lists of lists; the node itself when it gives none.</summary>
    private static RuleNode Element(RuleNode node)
    {
        var current = node;
        for (var level = 0; level < MaxNestedLists && current.Items is { } items; level++)
        {
            current = items;
        }

        return current;
    }

    private static ValueExpectation Describe(RuleNode node, SchemaDocs docs, bool required, string at, OfficialExample? example)
    {
        var types = node.Types.ToList();
        if (node.AllowsNull)
        {
            types.Add("null");
        }

        var (allowed, allowedCount) = AllowedOf(node);
        var examples = docs.Examples.Concat(node.Docs.Examples).Distinct(StringComparer.Ordinal).Take(SchemaDocs.MaxExamples).ToList();
        var expectation = new ValueExpectation
        {
            At = at,
            Title = docs.Title ?? node.Docs.Title,
            Description = docs.Description ?? node.Docs.Description,
            Summary = string.Empty,
            Types = types,
            Required = required,
            Patterns = node.Patterns.Select(p => p.Text).ToList(),
            Allowed = allowed,
            AllowedCount = allowedCount,
            Formats = [.. node.Formats],
            MinLength = node.MinLength,
            MaxLength = node.MaxLength,
            MinItems = node.MinItems,
            MaxItems = node.MaxItems,
            Minimum = node.Minimum,
            Maximum = node.Maximum,
            ExclusiveMinimum = node.ExclusiveMinimum,
            ExclusiveMaximum = node.ExclusiveMaximum,
            MultipleOf = node.MultiplesOf.Select(m => m.Text).ToList(),
            UniqueItems = node.UniqueItems,
            EntityTypes = [.. node.Relationships],
            Properties = node.Properties.Keys.Take(MaxProperties).ToList(),
            PropertyCount = node.Properties.Count,
            RequiredProperties = [.. node.Required],
            OnlyNamedProperties = node.AdditionalForbidden,
            Items = node.Items is not null && node.MayBeArray ? ItemsPhrase(Element(node)) : null,
            Forms = node.Choices.Count == 0 ? 0 : node.Choices[0].Count,
            Examples = examples,
            OsduExample = example is null ? null : ExampleValue(example.Record, at),
        };
        return expectation with { Summary = Summarize(expectation) };
    }

    private static (List<string> Allowed, int Count) AllowedOf(RuleNode node)
    {
        if (node.Constants.Count > 0)
        {
            return ([Text(node.Constants[0])], 1);
        }

        if (node.Enumerations.Count == 0)
        {
            return ([], 0);
        }

        // Every enumeration holds, so the values allowed are those all of them list.
        var values = node.Enumerations[0]
            .Where(value => node.Enumerations.Skip(1).All(other => other.Any(option => JsonNode.DeepEquals(option, value))))
            .ToList();
        return (values.Take(MaxAllowed).Select(Text).ToList(), values.Count);
    }

    private static string ItemsPhrase(RuleNode item)
    {
        var (_, allowedCount) = AllowedOf(item);
        var core = item.Relationships.Count > 0 ? $"ids of {Or(item.Relationships)} records"
            : allowedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"values from a list of {allowedCount}")
            : item.Types.Count == 0 ? "values of any kind"
            : string.Join(" or ", item.Types.Select(Plural));
        return item.Docs.Title is { } title && item.Relationships.Count == 0 ? $"{core} ({title})" : core;
    }

    private static string Summarize(ValueExpectation e)
    {
        string core;
        if (e.EntityTypes.Count > 0)
        {
            core = $"the id of a {Or(e.EntityTypes)} record";
        }
        else if (e.AllowedCount == 1)
        {
            core = $"exactly {e.Allowed[0]}";
        }
        else if (e.AllowedCount > 1)
        {
            core = string.Create(CultureInfo.InvariantCulture, $"one of {e.AllowedCount} values");
        }
        else
        {
            var named = e.Types.Where(t => t != "null").ToList();
            core = named.Count == 0 ? "any value" : string.Join(" or ", named.Select(t => Phrase(t, e)));
        }

        var qualifiers = new List<string>();
        if (e.EntityTypes.Count == 0 && e.Patterns.Count > 0)
        {
            qualifiers.Add(e.Patterns.Count == 1 ? $"matching {e.Patterns[0]}" : string.Create(CultureInfo.InvariantCulture, $"matching {e.Patterns[0]} and {e.Patterns.Count - 1} more pattern(s)"));
        }

        if (Range(e.MinLength, e.MaxLength, "character") is { } length)
        {
            qualifiers.Add($"of {length}");
        }

        if (NumberRange(e) is { } numbers)
        {
            qualifiers.Add(numbers);
        }

        if (Range(e.MinItems, e.MaxItems, "item") is { } count)
        {
            qualifiers.Add($"with {count}");
        }

        if (e.MultipleOf.Count > 0)
        {
            qualifiers.Add($"in steps of {e.MultipleOf[0]}");
        }

        if (e.UniqueItems)
        {
            qualifiers.Add("each item once");
        }

        var summary = qualifiers.Count == 0 ? core : $"{core} {string.Join(", ", qualifiers)}";
        return e.Types.Contains("null") ? summary + ", or null" : summary;
    }

    private static string Phrase(string type, ValueExpectation e) => type switch
    {
        "array" => e.Items is { } items ? $"a list of {items}" : "a list",
        "object" => e.Title is { } title ? $"an object ({title})" : "an object",
        "string" => FormatPhrase(e.Formats) ?? "text",
        "integer" => "a whole number",
        "number" => "a number",
        "boolean" => "true or false",
        _ => type,
    };

    private static string Plural(string type) => type switch
    {
        "array" => "lists",
        "object" => "objects",
        "string" => "text values",
        "integer" => "whole numbers",
        "number" => "numbers",
        "boolean" => "true or false values",
        _ => type + " values",
    };

    private static string? FormatPhrase(IReadOnlyList<string> formats) => (formats.Count == 0 ? null : formats[0]) switch
    {
        "date-time" => "a date and time (RFC 3339)",
        "date" => "a date (RFC 3339)",
        "time" => "a time (RFC 3339)",
        "uri" => "a URI",
        "uri-reference" => "a URI reference",
        "email" => "an email address",
        "uuid" => "a UUID",
        "ipv4" => "an IPv4 address",
        "ipv6" => "an IPv6 address",
        _ => null,
    };

    private static string? Range(long? least, long? most, string unit)
    {
        static string Units(long count, string unit) => count == 1 ? unit : unit + "s";
        return (least, most) switch
        {
            ({ } low, { } high) when low == high => string.Create(CultureInfo.InvariantCulture, $"exactly {low} {Units(low, unit)}"),
            ({ } low, { } high) => string.Create(CultureInfo.InvariantCulture, $"{low} to {high} {Units(high, unit)}"),
            ({ } low, null) => string.Create(CultureInfo.InvariantCulture, $"at least {low} {Units(low, unit)}"),
            (null, { } high) => string.Create(CultureInfo.InvariantCulture, $"at most {high} {Units(high, unit)}"),
            _ => null,
        };
    }

    private static string? NumberRange(ValueExpectation e)
    {
        if (e.Minimum is { } low && e.Maximum is { } high)
        {
            return $"from {Number(low)} to {Number(high)}";
        }

        var lower = e.ExclusiveMinimum is { } above ? $"above {Number(above)}" : e.Minimum is { } least ? $"at least {Number(least)}" : null;
        var upper = e.ExclusiveMaximum is { } below ? $"below {Number(below)}" : e.Maximum is { } most ? $"at most {Number(most)}" : null;
        return (lower, upper) switch
        {
            ({ } l, { } u) => $"{l} and {u}",
            ({ } l, null) => l,
            (null, { } u) => u,
            _ => null,
        };
    }

    /// <summary>The value found where a finding is, in words, the node itself when the record holds it, and whether it was read from the record.</summary>
    private static (string Text, JsonNode? Node, bool Known) Found(SchemaFinding finding, JsonNode? record)
    {
        if (finding.Rule == "required")
        {
            return ("absent", null, true);
        }

        if (record is not null && NodeAt(record, finding.Path) is (true, var node))
        {
            return (Words(node), node, true);
        }

        return (Clip(finding.Value, MaxFound), null, false);
    }

    /// <summary>The value at an exact path (<c>data.Curves[2].CurveID</c>), and whether the record holds anything there.</summary>
    private static (bool Exists, JsonNode? Node) NodeAt(JsonNode record, string path)
    {
        var current = record;
        if (path.Length == 0)
        {
            return (true, current);
        }

        if (!RecordPathNotation.TryParse(path, out var steps, out _))
        {
            return (false, null);
        }

        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case RecordPathStepKind.Property when current is JsonObject obj && obj.TryGetPropertyValue(step.Name!, out var child):
                    current = child;
                    break;
                case RecordPathStepKind.Index when current is JsonArray list && step.Index < list.Count:
                    current = list[step.Index];
                    break;
                default:
                    return (false, null);
            }
        }

        return (true, current);
    }

    /// <summary>The value the example record holds at a place as a finding names it, the first item of each list taken.</summary>
    private static string? ExampleValue(JsonObject record, string at)
    {
        JsonNode? current = record;
        if (at.Length > 0)
        {
            if (PlaceSteps(at) is not { } steps)
            {
                return null;
            }

            foreach (var (name, inItem) in steps)
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out var child))
                {
                    return null;
                }

                current = child;

                if (inItem)
                {
                    if (current is not JsonArray { Count: > 0 } list)
                    {
                        return null;
                    }

                    current = list[0];
                }
            }
        }

        return Clip(Text(current), MaxOsduExample);
    }

    private static string? Advise(SchemaFinding finding, ValueExpectation? e, ValueExpectation? holder, JsonNode? node, bool known)
    {
        var name = NameOf(finding);
        var example = ExampleClause(e);
        switch (finding.Rule)
        {
            case "type":
                var isNull = known ? node is null : finding.Value == "null";
                if (isNull)
                {
                    return e is null ? $"Leave {name} out, or give it a value: the schema does not take null here."
                        : e.Required ? $"Give {name} {e.Summary}: the schema requires {name} and does not take null for it.{example}"
                        : $"Leave {name} out, or give it {e.Summary}: the schema does not take null here, and {name} is not required.{example}";
                }

                return $"Write {name} as {e?.Summary ?? "the type the schema names"}.{TypeHint(node, e)}{example}";
            case "required":
                return $"Add {name}: the schema requires it, and it takes {e?.Summary ?? "a value"}.{example}";
            case "pattern":
                var pattern = e?.Patterns.FirstOrDefault(p => finding.Message.EndsWith(p, StringComparison.Ordinal)) ?? (e is { Patterns.Count: > 0 } ? e.Patterns[0] : null);
                if (e is { EntityTypes.Count: > 0 })
                {
                    return $"Write the id of a {Or(e.EntityTypes)} record as OSDU writes ids, <partition>:<entity type>:<code>: with the closing colon or a version{(pattern is null ? "" : $", so it matches {pattern}")}.{example}";
                }

                return pattern is null ? null : $"Change {name} so it matches {pattern}.{example}";
            case "enum":
                return e is { AllowedCount: > 0 } ? $"Use one of the values the schema allows: {Listed(e.Allowed, e.AllowedCount)}." : null;
            case "const":
                return e is { AllowedCount: 1 } ? $"Write {name} as exactly {e.Allowed[0]}." : null;
            case "format":
                return FormatAdvice(name, e);
            case "minLength" or "maxLength":
                return Range(e?.MinLength, e?.MaxLength, "character") is { } length ? $"Write {name} with {length}.{example}" : null;
            case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum":
                return e is not null && NumberRange(e) is { } numbers ? $"Write {name} as a number {numbers}." : null;
            case "multipleOf":
                return e is { MultipleOf.Count: > 0 } ? $"Write {name} as a multiple of {e.MultipleOf[0]}." : null;
            case "minItems" or "maxItems":
                return Range(e?.MinItems, e?.MaxItems, "item") is { } items ? $"Give {name} {items}." : null;
            case "uniqueItems":
                return $"Remove the repeated item from {name}: the schema takes each item once.";
            case "additionalProperties":
                var allowed = holder is { Properties.Count: > 0 } ? $" ({Listed(holder.Properties, holder.PropertyCount)})" : string.Empty;
                var extension = holder is not null && holder.Properties.Contains("ExtensionProperties", StringComparer.Ordinal)
                    ? $" A property of your own belongs under {Readable(HolderOf(finding.At))}.ExtensionProperties."
                    : string.Empty;
                return $"Remove {name}, or name it as one of the properties the schema describes{allowed}: the object holding it allows no other.{extension}";
            case "relationship":
                var types = e is { EntityTypes.Count: > 0 } ? Or(e.EntityTypes) : "the entity type the schema names";
                return finding.Message.Contains("is not an OSDU id", StringComparison.Ordinal)
                    ? $"Write the id of a {types} record, not its name or code: <partition>:<entity type>:<code>: with the closing colon or a version. The cache of the reference data, or a lookup, gives a code its id.{example}"
                    : $"Point {name} to a {types} record; the id written names a record of another type.{example}";
            case "reference":
                return $"Deliver the record {Clip(finding.Value, ExampleInAdvice)} to OSDU before this one, or correct the id: its partition, entity type and code must name a record OSDU holds.";
            case "anyOf":
                return e is { Forms: > 0 }
                    ? string.Create(CultureInfo.InvariantCulture, $"Give {name} the shape of one of the {e.Forms} forms the schema allows; the message says where the first form differs.")
                    : $"Give {name} the shape of one of the forms the schema allows; the message says where the first form differs.";
            default:
                return null;
        }
    }

    private static string? NotCheckedAdvice(SchemaFinding finding) => finding.Rule switch
    {
        "schema" => "Nothing to change in the record for this: the schema could not be read here, so the value was not checked.",
        "pattern" => "Nothing to change in the record for this: the schema's pattern cannot be run, so compare the value with it by eye.",
        "budget" or "depth" or "items" => "Nothing to change in the record for this: the check stops at its bounds, and this part lies past them.",
        "error" => "The check itself failed here. Check again; if it repeats, the message is the error to report.",
        var keyword when UncheckedKeywords.Contains(keyword, StringComparer.Ordinal)
            => $"The check does not apply the schema's '{keyword}'; read the schema's description of this part to be sure the value meets it.",
        _ => null,
    };

    private static string TypeHint(JsonNode? node, ValueExpectation? e)
    {
        if (node is null || e is null)
        {
            return string.Empty;
        }

        var takes = e.Types;
        bool Takes(string type) => takes.Contains(type, StringComparer.Ordinal);
        return node switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String && (Takes("number") || Takes("integer"))
                && double.TryParse(v.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                => $" Write the number itself, without quotes: {v.GetValue<string>().Trim()}.",
            JsonValue v when v.GetValueKind() == JsonValueKind.String && Takes("boolean")
                && v.GetValue<string>().Trim() is var flag && (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag.Equals("false", StringComparison.OrdinalIgnoreCase))
                => " Write true or false without quotes.",
            JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False && Takes("string")
                => $" Write it as text, in quotes: \"{v.ToJsonString()}\".",
            JsonValue v when v.GetValueKind() == JsonValueKind.Number && Takes("integer") && !Takes("number")
                => " Give a whole number; the schema takes no fraction here.",
            JsonArray when !Takes("array") && takes.Count > 0 => " Give a single value, not a list.",
            JsonObject when !Takes("object") && takes.Count > 0 => " Give a single value, not an object.",
            not JsonArray when Takes("array") => " Put the value in a list, a list of one if there is one value.",
            _ => string.Empty,
        };
    }

    private static string? FormatAdvice(string name, ValueExpectation? e) => e?.Formats.FirstOrDefault(f => f is not ("int32" or "int64")) switch
    {
        "date-time" => $"Write {name} as an RFC 3339 date and time with an offset or Z, such as 2026-09-01T10:15:30Z.",
        "date" => $"Write {name} as an RFC 3339 date, such as 2026-09-01.",
        "time" => $"Write {name} as an RFC 3339 time, such as 10:15:30Z.",
        "uri" => $"Write {name} as an absolute URI, such as https://example.org/path.",
        "uri-reference" => $"Write {name} as a URI or a reference relative to one.",
        "email" => $"Write {name} as an email address, such as name@example.org.",
        "uuid" => $"Write {name} as a UUID, such as 0f8fad5b-d9cb-469f-a165-70867728950e.",
        "ipv4" => $"Write {name} as an IPv4 address, such as 192.0.2.10.",
        "ipv6" => $"Write {name} as an IPv6 address, such as 2001:db8::1.",
        _ when e?.Formats.FirstOrDefault(f => f is "int32" or "int64") is { } integer => $"Write {name} as a whole number within the range of an {integer} integer.",
        _ => null,
    };

    /// <summary>One example to quote: the schema's first, else the data definitions' example value.</summary>
    private static string ExampleClause(ValueExpectation? e)
    {
        var example = e is { Examples.Count: > 0 } ? e.Examples[0] : e?.OsduExample;
        return example is null ? string.Empty : $" For example: {Clip(example, ExampleInAdvice)}";
    }

    /// <summary>The property a finding names, by its last step (<c>VerticalCRSID</c>, <c>Curves[2]</c>); the record for the record itself.</summary>
    private static string NameOf(SchemaFinding finding)
    {
        var path = finding.Path.Length > 0 ? finding.Path : finding.At;
        if (path.Length == 0)
        {
            return "the record";
        }

        return RecordPathNotation.LastName(path);
    }

    private static string Readable(string at)
        => at.Length == 0
            ? "the record"
            : RecordPathNotation.TryParse(at, out var steps, out _)
                ? RecordPathNotation.Write(steps.Where(s => s.Kind != RecordPathStepKind.Items))
                : at.Replace("[]", string.Empty, StringComparison.Ordinal);

    private static string Listed(IReadOnlyList<string> values, int count)
    {
        var listed = string.Join(", ", values.Take(ListedInAdvice));
        return count > ListedInAdvice ? string.Create(CultureInfo.InvariantCulture, $"{listed} and {count - ListedInAdvice} more") : listed;
    }

    private static string Or(IReadOnlyList<string> names)
        => names.Count <= 1 ? string.Join(string.Empty, names) : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}";

    /// <summary>A value as words: text in single quotes, a list or an object by its size, anything else as JSON.</summary>
    private static string Words(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => $"'{Clip(v.GetValue<string>(), MaxFound)}'",
        JsonArray list => string.Create(CultureInfo.InvariantCulture, $"a list of {list.Count} item{(list.Count == 1 ? "" : "s")}"),
        JsonObject obj => string.Create(CultureInfo.InvariantCulture, $"an object of {obj.Count} propert{(obj.Count == 1 ? "y" : "ies")}"),
        _ => Clip(node.ToJsonString(), MaxFound),
    };

    /// <summary>A value as text: text as it is, anything else as JSON.</summary>
    private static string Text(JsonNode? value)
        => value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "null";

    private static string Number(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    private static string Clip(string text, int longest) => text.Length <= longest ? text : text[..longest] + "...";
}
