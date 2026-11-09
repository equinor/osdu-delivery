using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// A template's JSON Schema compiled once into the rules a value is checked against (<see cref="RuleNode"/>), shared by every
/// check of that template version: a value a render wrote, a whole record before it is sent, a record OSDU holds. Each
/// schema node is compiled once: its <c>$ref</c> followed, its <c>allOf</c> branches combined, its patterns compiled. A
/// check then walks the value and the rules side by side and merges or copies nothing.
/// </summary>
/// <remarks>
/// <c>allOf</c> branches are combined as JSON Schema means them, every branch holding: the properties of all of them, the
/// required names of all of them, every pattern, enumeration and constant of each, and the tightest of their bounds. One
/// exception keeps OSDU's schemas usable: <c>additionalProperties: false</c> in one branch judges a property against the
/// properties every branch declares, not its own branch's alone, since OSDU composes a record's data from several
/// branches. A <c>$ref</c> the bundle does not hold, or a chain of references that never reaches a schema, compiles to a
/// node that says why it cannot be checked (<see cref="RuleNode.Unchecked"/>), so a check reports that part as not
/// checked instead of failing.
/// </remarks>
public sealed class SchemaRules
{
    /// <summary>How deep a chain of references and branches is followed while compiling.</summary>
    private const int MaxCompileDepth = 128;

    /// <summary>The keywords of JSON Schema draft-07 that constrain a value and that the rules do not check.</summary>
    private static readonly string[] UncheckedKeywords =
        ["not", "if", "contains", "patternProperties", "propertyNames", "dependencies", "additionalItems", "minProperties", "maxProperties"];

    private static readonly ConditionalWeakTable<SchemaSnapshot, SchemaRules> Cache = new();

    private readonly ConcurrentDictionary<string, RuleNode?> _paths = new(StringComparer.Ordinal);
    private readonly JsonObject _root;
    private readonly JsonObject? _definitions;
    private readonly Dictionary<JsonObject, RuleNode> _compiled = new(ReferenceEqualityComparer.Instance);
    private readonly SortedSet<string> _notes = new(StringComparer.Ordinal);

    private SchemaRules(SchemaSnapshot schema)
    {
        Kind = schema.Kind;
        Version = schema.Version;
        _root = schema.Root;
        _definitions = schema.Root["definitions"] as JsonObject ?? schema.Root["$defs"] as JsonObject;
        Root = Compile(schema.Root, 0);
        Notes = _notes.ToList();
        Nodes = _compiled.Count;

        // What compiling needed is let go; the rules hold the nodes they reach.
        _compiled.Clear();
    }

    /// <summary>The kind whose schema this is.</summary>
    public string Kind { get; }

    /// <summary>The template's content version (<see cref="SchemaSnapshot.Version"/>).</summary>
    public string Version { get; }

    /// <summary>The rules of the record as a whole.</summary>
    public RuleNode Root { get; }

    /// <summary>
    /// What the schema states that no check asserts, once each: a format the rules do not know, a pattern neither dialect
    /// reads, a keyword the rules do not check, a reference that does not resolve.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>How many schema nodes were compiled.</summary>
    public int Nodes { get; }

    /// <summary>The rules of <paramref name="schema"/>, compiled on first use and kept as long as the snapshot is.</summary>
    public static SchemaRules Of(SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Cache.GetValue(schema, static s => new SchemaRules(s));
    }

    /// <summary>
    /// The rules of the property at <paramref name="dottedPath"/> from the record root (<c>data.Curves.CurveID</c>, a list of
    /// objects stepped into implicitly), found as <see cref="SchemaSnapshot.Resolve"/> finds a property: by its name, else
    /// by the schema an object gives every other property, else as any value where the object allows any property. Null
    /// when the schema describes no such property.
    /// </summary>
    public RuleNode? At(string dottedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);
        return _paths.GetOrAdd(dottedPath, AtUncached);
    }

    private RuleNode? AtUncached(string dottedPath)
    {
        if (RecordPathNotation.Names(dottedPath) is not { } names)
        {
            return null;
        }

        var current = Root;
        RuleNode? result = null;
        foreach (var segment in names)
        {
            var next = current.Property(segment);
            if (next is null)
            {
                return null;
            }

            result = next;
            current = next.Items is { } items && next.MayBeArray ? items : next;
        }

        return result;
    }

    private RuleNode Compile(JsonObject raw, int depth)
    {
        if (_compiled.TryGetValue(raw, out var known))
        {
            return known;
        }

        if (depth > MaxCompileDepth)
        {
            return Unchecked(raw, string.Create(CultureInfo.InvariantCulture, $"the schema nests references more than {MaxCompileDepth} deep here"));
        }

        if (raw["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
        {
            // A reference stands for the schema it names; JSON Schema draft-07 ignores whatever sits beside it.
            if (Definition(reference) is not { } target)
            {
                return Unchecked(raw, $"the schema refers to '{reference}', which its bundle does not hold");
            }

            if (ReferenceEquals(target, raw))
            {
                return Unchecked(raw, $"the schema's reference '{reference}' names itself");
            }

            var resolved = Compile(target, depth + 1);
            _compiled[raw] = resolved;
            return resolved;
        }

        var node = new RuleNode();
        _compiled[raw] = node;
        node.Filling = true;
        Own(node, raw, depth);
        if (raw["allOf"] is JsonArray allOf)
        {
            foreach (var branch in allOf.OfType<JsonObject>())
            {
                var compiled = Compile(branch, depth + 1);
                if (compiled.Filling)
                {
                    node.Unchecked ??= "the schema's allOf refers back to the schema it is part of";
                    _notes.Add(node.Unchecked);
                    continue;
                }

                node.Combine(compiled);
            }

            // A schema built of branches is an object unless a branch says otherwise, as the template view reads it.
            if (!node.TypeDeclared)
            {
                node.Types = ["object"];
                node.TypeDeclared = true;
            }
        }

        node.Filling = false;
        return node;
    }

    /// <summary>What <paramref name="raw"/> itself states, its <c>allOf</c> aside.</summary>
    private void Own(RuleNode node, JsonObject raw, int depth)
    {
        node.Docs = SchemaDocs.Of(raw);
        switch (raw["type"])
        {
            case JsonValue single when single.TryGetValue<string>(out var type):
                node.TypeDeclared = true;
                node.AllowsNull = type == "null";
                node.Types = type == "null" ? [] : [type];
                break;
            case JsonArray many:
                var names = many.OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var name) ? name : null).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                node.TypeDeclared = true;
                node.AllowsNull = names.Contains("null");
                node.Types = names.Where(t => t != "null").ToList();
                break;
        }

        if (raw.TryGetPropertyValue("const", out var constant))
        {
            node.Constants.Add(constant?.DeepClone());
        }

        if (raw["enum"] is JsonArray allowed)
        {
            node.Enumerations.Add((JsonArray)allowed.DeepClone());
        }

        if (raw["pattern"] is JsonValue patternNode && patternNode.TryGetValue<string>(out var pattern))
        {
            var regex = SchemaPatterns.Compile(pattern);
            node.Patterns.Add(new RulePattern(pattern, regex));
            if (regex is null)
            {
                _notes.Add($"the pattern {pattern} is read by neither ECMAScript nor .NET, so values are not checked against it");
            }
        }

        node.MinLength = Tightest(node.MinLength, Whole(raw, "minLength"), Math.Max);
        node.MaxLength = Tightest(node.MaxLength, Whole(raw, "maxLength"), Math.Min);
        node.MinItems = Tightest(node.MinItems, Whole(raw, "minItems"), Math.Max);
        node.MaxItems = Tightest(node.MaxItems, Whole(raw, "maxItems"), Math.Min);
        node.Minimum = Tightest(node.Minimum, Bound(raw, "minimum"), Math.Max);
        node.Maximum = Tightest(node.Maximum, Bound(raw, "maximum"), Math.Min);
        node.ExclusiveMinimum = Tightest(node.ExclusiveMinimum, Bound(raw, "exclusiveMinimum"), Math.Max);
        node.ExclusiveMaximum = Tightest(node.ExclusiveMaximum, Bound(raw, "exclusiveMaximum"), Math.Min);
        if (raw["multipleOf"] is JsonValue step
            && decimal.TryParse(step.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var multiple) && multiple > 0)
        {
            node.MultiplesOf.Add(new RuleMultiple(step.ToJsonString(), multiple));
        }

        if (raw["format"] is JsonValue formatNode && formatNode.TryGetValue<string>(out var format))
        {
            node.Formats.Add(format);
            if (!TemplateValueRules.AssertsFormat(format) && format is not ("int32" or "int64"))
            {
                _notes.Add($"the format '{format}' is not one the checks assert, so values are not checked against it");
            }
        }

        if (raw["uniqueItems"] is JsonValue unique && unique.TryGetValue<bool>(out var distinct) && distinct)
        {
            node.UniqueItems = true;
        }

        switch (raw["items"])
        {
            case JsonObject items:
                node.Items ??= Compile(items, depth + 1);
                break;
            case JsonArray:
                node.ItemsUnchecked = "the schema gives the items of this list one schema each by position, which the checks do not apply";
                _notes.Add(node.ItemsUnchecked);
                break;
        }

        if (raw["properties"] is JsonObject properties)
        {
            foreach (var (name, child) in properties)
            {
                if (child is JsonObject schema)
                {
                    node.AddProperty(name, Compile(schema, depth + 1));
                    node.AddPropertyDocs(name, SchemaDocs.Of(schema));
                }
            }
        }

        if (raw["required"] is JsonArray required)
        {
            foreach (var name in required.OfType<JsonValue>().Select(n => n.TryGetValue<string>(out var text) ? text : null).OfType<string>())
            {
                node.AddRequired(name);
            }
        }

        switch (raw["additionalProperties"])
        {
            case JsonObject additional:
                node.Additional ??= Compile(additional, depth + 1);
                break;
            case JsonValue flag when flag.TryGetValue<bool>(out var open):
                if (open)
                {
                    node.AdditionalAllowed = true;
                }
                else
                {
                    node.AdditionalForbidden = true;
                }

                break;
        }

        foreach (var keyword in (ReadOnlySpan<string>)["oneOf", "anyOf"])
        {
            if (raw[keyword] is JsonArray forms && forms.Count > 0)
            {
                node.Choices.Add(forms.OfType<JsonObject>().Select(form => Compile(form, depth + 1)).ToList());
            }
        }

        foreach (var relationship in OsduTemplate.Relationships(raw, raw))
        {
            if (!node.Relationships.Contains(relationship, StringComparer.Ordinal))
            {
                node.Relationships.Add(relationship);
            }
        }

        foreach (var keyword in UncheckedKeywords)
        {
            if (raw.ContainsKey(keyword) && !node.UncheckedKeywords.Contains(keyword, StringComparer.Ordinal))
            {
                node.UncheckedKeywords.Add(keyword);
                _notes.Add($"the schema's '{keyword}' is not checked");
            }
        }
    }

    /// <summary>The schema a local reference names, or null when the bundle does not hold it.</summary>
    private JsonObject? Definition(string reference)
    {
        // A reference to the whole schema (a bundle whose part refers back to the kind it bundles).
        if (reference == "#")
        {
            return _root;
        }

        foreach (var prefix in (ReadOnlySpan<string>)["#/definitions/", "#/$defs/"])
        {
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                return _definitions?[reference[prefix.Length..]] as JsonObject;
            }
        }

        return null;
    }

    private RuleNode Unchecked(JsonObject raw, string why)
    {
        var node = new RuleNode { Unchecked = why };
        _compiled[raw] = node;
        _notes.Add(why);
        return node;
    }

    private static long? Whole(JsonObject raw, string keyword)
        => raw[keyword] is JsonValue node && long.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? whole : null;

    /// <summary>A number a keyword gives, or null; a draft-04 boolean exclusive bound gives none.</summary>
    private static double? Bound(JsonObject raw, string keyword)
        => raw[keyword] is JsonValue node && node.GetValueKind() == JsonValueKind.Number
            && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bound) ? bound : null;

    private static T? Tightest<T>(T? held, T? given, Func<T, T, T> pick)
        where T : struct
        => held is { } a && given is { } b ? pick(a, b) : held ?? given;
}

/// <summary>A pattern a text value must match, with its compiled form, or null when neither dialect reads it.</summary>
public sealed record RulePattern(string Text, Regex? Regex);

/// <summary>A step a number must be a whole multiple of, as the schema writes it and as a decimal.</summary>
public sealed record RuleMultiple(string Text, decimal Step);

/// <summary>
/// What a schema says of a value in words, beside its rules: its <c>title</c>, its <c>description</c>, and the values its
/// <c>example</c> and <c>examples</c> give, each bounded so a schema of any size compiles to a bounded set of words.
/// </summary>
public sealed record SchemaDocs(string? Title, string? Description, IReadOnlyList<string> Examples)
{
    /// <summary>The longest title kept.</summary>
    public const int MaxTitle = 200;

    /// <summary>The longest description kept.</summary>
    public const int MaxDescription = 1_000;

    /// <summary>The most examples kept.</summary>
    public const int MaxExamples = 3;

    /// <summary>The longest example kept.</summary>
    public const int MaxExample = 200;

    public static SchemaDocs None { get; } = new(null, null, []);

    /// <summary>The words <paramref name="raw"/> states of itself.</summary>
    public static SchemaDocs Of(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var examples = new List<string>();
        if (raw.TryGetPropertyValue("example", out var example))
        {
            Add(examples, example);
        }

        if (raw["examples"] is JsonArray many)
        {
            foreach (var item in many)
            {
                Add(examples, item);
            }
        }

        var docs = new SchemaDocs(Words(raw["title"], MaxTitle), Words(raw["description"], MaxDescription), examples);
        return docs.Title is null && docs.Description is null && docs.Examples.Count == 0 ? None : docs;
    }

    /// <summary>These words, with what <paramref name="other"/> says filling what these leave unsaid.</summary>
    public SchemaDocs Or(SchemaDocs other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(other, None) ? this : ReferenceEquals(this, None) ? other
            : new SchemaDocs(Title ?? other.Title, Description ?? other.Description, Examples.Count > 0 ? Examples : other.Examples);
    }

    private static void Add(List<string> examples, JsonNode? example)
    {
        if (examples.Count >= MaxExamples)
        {
            return;
        }

        var text = example is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : example?.ToJsonString() ?? "null";
        text = Clip(text, MaxExample);
        if (text.Length > 0 && !examples.Contains(text, StringComparer.Ordinal))
        {
            examples.Add(text);
        }
    }

    private static string? Words(JsonNode? node, int longest)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text ? Clip(text, longest) : null;

    private static string Clip(string text, int longest) => text.Length <= longest ? text : text[..longest] + "...";
}

/// <summary>
/// The rules one schema node states, with every <c>$ref</c> followed and every <c>allOf</c> branch combined
/// (<see cref="SchemaRules"/>). Built while its schema compiles and read-only afterwards, so any number of checks share it.
/// </summary>
public sealed class RuleNode
{
    private static readonly RuleNode AnyValue = new();

    private readonly Dictionary<string, RuleNode> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SchemaDocs> _propertyDocs = new(StringComparer.Ordinal);
    private readonly List<string> _required = [];

    internal RuleNode()
    {
    }

    /// <summary>Why nothing at this node can be checked, or null when it can.</summary>
    public string? Unchecked { get; internal set; }

    /// <summary>What the schema says of a value here in words: its title, description and examples.</summary>
    public SchemaDocs Docs { get; internal set; } = SchemaDocs.None;

    /// <summary>The JSON types a value may be, <c>null</c> aside; empty allows any.</summary>
    public IReadOnlyList<string> Types { get; internal set; } = [];

    /// <summary>Whether the schema names any type at all, so a <c>null</c> it does not name breaks it.</summary>
    public bool TypeDeclared { get; internal set; }

    /// <summary>Whether <c>null</c> is among the types the schema names.</summary>
    public bool AllowsNull { get; internal set; }

    /// <summary>The values a value must equal, one per <c>const</c>.</summary>
    public List<JsonNode?> Constants { get; } = [];

    /// <summary>The lists a value must be one of, one per <c>enum</c>.</summary>
    public List<JsonArray> Enumerations { get; } = [];

    public List<RulePattern> Patterns { get; } = [];

    public long? MinLength { get; internal set; }

    public long? MaxLength { get; internal set; }

    public long? MinItems { get; internal set; }

    public long? MaxItems { get; internal set; }

    public double? Minimum { get; internal set; }

    public double? Maximum { get; internal set; }

    public double? ExclusiveMinimum { get; internal set; }

    public double? ExclusiveMaximum { get; internal set; }

    public List<RuleMultiple> MultiplesOf { get; } = [];

    public List<string> Formats { get; } = [];

    public bool UniqueItems { get; internal set; }

    /// <summary>The rules every item of a list is held to, or null when the schema gives none.</summary>
    public RuleNode? Items { get; internal set; }

    /// <summary>Why the items of a list are not checked one by one, or null.</summary>
    public string? ItemsUnchecked { get; internal set; }

    public IReadOnlyDictionary<string, RuleNode> Properties => _properties;

    public IReadOnlyList<string> Required => _required;

    /// <summary>The rules every property the schema does not name is held to, or null.</summary>
    public RuleNode? Additional { get; internal set; }

    /// <summary>Whether the schema says outright that an object may hold properties it does not name.</summary>
    public bool AdditionalAllowed { get; internal set; }

    /// <summary>Whether the schema allows no property it does not name.</summary>
    public bool AdditionalForbidden { get; internal set; }

    /// <summary>Each <c>oneOf</c> or <c>anyOf</c>: the forms a value must match one of.</summary>
    public List<IReadOnlyList<RuleNode>> Choices { get; } = [];

    /// <summary>The entity types an OSDU id written here may be of (<c>x-osdu-relationship</c>).</summary>
    public List<string> Relationships { get; } = [];

    /// <summary>The keywords this node states that the checks do not apply.</summary>
    public List<string> UncheckedKeywords { get; } = [];

    /// <summary>Whether a value here may be a list: the schema names no type, or names <c>array</c>.</summary>
    public bool MayBeArray => Types.Count == 0 || Types.Contains("array");

    /// <summary>True while the node is being compiled, so a branch that refers back to it is not combined into it.</summary>
    internal bool Filling { get; set; }

    /// <summary>
    /// The rules of the property <paramref name="name"/> of an object here: its own, else those every other property is held
    /// to, else any value where the object allows any property. Null when the schema describes no such property.
    /// </summary>
    public RuleNode? Property(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _properties.TryGetValue(name, out var declared) ? declared : Additional ?? (AdditionalAllowed ? AnyValue : null);
    }

    internal void AddProperty(string name, RuleNode rules)
    {
        if (!_properties.TryGetValue(name, out var held))
        {
            _properties[name] = rules;
        }
        else if (!ReferenceEquals(held, rules))
        {
            // Two branches each describe the property: a value of it holds to both.
            var both = new RuleNode();
            both.Combine(held);
            both.Combine(rules);
            _properties[name] = both;
        }
    }

    /// <summary>
    /// What the schema says in words of the property <paramref name="name"/> where an object here names it: the words
    /// written beside the property, which OSDU writes beside a <c>$ref</c> as well, else what the property's schema says of
    /// itself. Null when the object names no such property.
    /// </summary>
    public SchemaDocs? PropertyDocs(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var beside = _propertyDocs.GetValueOrDefault(name);
        var own = _properties.GetValueOrDefault(name)?.Docs;
        return beside is null ? own : own is null ? beside : beside.Or(own);
    }

    internal void AddPropertyDocs(string name, SchemaDocs docs)
        => _propertyDocs[name] = _propertyDocs.TryGetValue(name, out var held) ? held.Or(docs) : docs;

    internal void AddRequired(string name)
    {
        if (!_required.Contains(name, StringComparer.Ordinal))
        {
            _required.Add(name);
        }
    }

    /// <summary>Adds what <paramref name="other"/> requires to what this node requires: both hold of a value.</summary>
    internal void Combine(RuleNode other)
    {
        if (other.Unchecked is { } why)
        {
            Unchecked ??= why;
        }

        Docs = Docs.Or(other.Docs);
        foreach (var (name, docs) in other._propertyDocs)
        {
            AddPropertyDocs(name, docs);
        }

        if (other.TypeDeclared)
        {
            if (!TypeDeclared)
            {
                Types = other.Types;
                AllowsNull = other.AllowsNull;
            }
            else
            {
                Types = Intersect(Types, other.Types);
                AllowsNull = AllowsNull && other.AllowsNull;
            }

            TypeDeclared = true;
        }

        Constants.AddRange(other.Constants);
        Enumerations.AddRange(other.Enumerations);
        Patterns.AddRange(other.Patterns.Where(p => !Patterns.Any(held => held.Text == p.Text)));
        MinLength = Max(MinLength, other.MinLength);
        MaxLength = Min(MaxLength, other.MaxLength);
        MinItems = Max(MinItems, other.MinItems);
        MaxItems = Min(MaxItems, other.MaxItems);
        Minimum = Max(Minimum, other.Minimum);
        Maximum = Min(Maximum, other.Maximum);
        ExclusiveMinimum = Max(ExclusiveMinimum, other.ExclusiveMinimum);
        ExclusiveMaximum = Min(ExclusiveMaximum, other.ExclusiveMaximum);
        MultiplesOf.AddRange(other.MultiplesOf.Where(m => !MultiplesOf.Any(held => held.Step == m.Step)));
        Formats.AddRange(other.Formats.Where(f => !Formats.Contains(f, StringComparer.Ordinal)));
        UniqueItems |= other.UniqueItems;
        if (other.Items is { } items)
        {
            if (Items is null || ReferenceEquals(Items, items))
            {
                Items = items;
            }
            else
            {
                var both = new RuleNode();
                both.Combine(Items);
                both.Combine(items);
                Items = both;
            }
        }

        ItemsUnchecked ??= other.ItemsUnchecked;
        foreach (var (name, rules) in other._properties)
        {
            AddProperty(name, rules);
        }

        foreach (var name in other._required)
        {
            AddRequired(name);
        }

        Additional ??= other.Additional;
        AdditionalAllowed |= other.AdditionalAllowed;
        AdditionalForbidden |= other.AdditionalForbidden;
        Choices.AddRange(other.Choices);
        Relationships.AddRange(other.Relationships.Where(r => !Relationships.Contains(r, StringComparer.Ordinal)));
        UncheckedKeywords.AddRange(other.UncheckedKeywords.Where(k => !UncheckedKeywords.Contains(k, StringComparer.Ordinal)));
    }

    /// <summary>The types both lists allow; an integer is a number, so integer and number leave integer.</summary>
    private static List<string> Intersect(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0)
        {
            return right.ToList();
        }

        if (right.Count == 0)
        {
            return left.ToList();
        }

        var both = new List<string>();
        foreach (var type in left)
        {
            if (right.Contains(type, StringComparer.Ordinal))
            {
                both.Add(type);
            }
            else if ((type == "integer" && right.Contains("number", StringComparer.Ordinal)) || (type == "number" && right.Contains("integer", StringComparer.Ordinal)))
            {
                both.Add("integer");
            }
        }

        // Branches that agree on no type leave none a value can have; the first branch's types stand, so a check still
        // names what the schema asks for rather than accepting anything.
        return both.Count == 0 ? left.ToList() : both.Distinct(StringComparer.Ordinal).ToList();
    }

    private static long? Max(long? a, long? b) => a is { } x && b is { } y ? Math.Max(x, y) : a ?? b;

    private static long? Min(long? a, long? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;

    private static double? Max(double? a, double? b) => a is { } x && b is { } y ? Math.Max(x, y) : a ?? b;

    private static double? Min(double? a, double? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;
}
