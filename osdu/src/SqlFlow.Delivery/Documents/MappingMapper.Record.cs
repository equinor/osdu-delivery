using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Expressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a mapping's <c>record</c> block (osdu/docs/reference/flow/mapping.md): a tree laid out the way the rendered
/// record is. Every word of the mapping language starts with <c>$</c> and every other key is a property of the record,
/// so no template's property name is ever read as the language, and no column's name either. A property is one of four
/// nodes. A literal is written as the value it is. An object is a map of property names. A value node reads one value
/// with <c>$from</c> (a column), <c>$expr</c> (an expression over the row), <c>$value</c> (a literal with settings),
/// <c>$cache</c> or <c>$search</c>, and takes its settings beside it. A <c>$forEach</c> node repeats the rows of a
/// child dataset as the items of an array, each item laid out under <c>$item</c>, and keeps only the rows its
/// <c>$where</c> holds for. A list holding nodes is a list of values, each item a literal or a value node, or a list of
/// objects, each item an object laid out as the tree is. A bare column name reads the row the node is in (under a
/// <c>$forEach</c>, the item's row), and <c>$dataset.&lt;column&gt;</c> the dataset's own row from anywhere. A property
/// whose own name starts with <c>$</c> is written with one more: <c>$$name</c>.
/// </summary>
internal static partial class MappingMapper
{
    /// <summary>What starts every word of the mapping language, so a key or a reference that starts with it is never a record property or a column.</summary>
    internal const string Marker = "$";

    internal const string FromKey = "$from";
    internal const string ExprKey = "$expr";
    internal const string ValueKey = "$value";
    internal const string CacheKey = "$cache";
    internal const string SearchKey = "$search";
    internal const string ForEachKey = "$forEach";
    internal const string ItemKey = "$item";
    internal const string FindByKey = "$findBy";
    internal const string ModifiersKey = "$modifiers";
    internal const string WhenKey = "$when";
    internal const string WhereKey = "$where";
    internal const string RequiredKey = "$required";
    internal const string IgnoreSeparatorsKey = "$ignoreSeparators";
    internal const string DescriptionKey = "$description";
    internal const string CoalesceKey = "$coalesce";
    internal const string UnverifiedKey = "$unverified";
    internal const string LookupKey = "$lookup";
    internal const string ParentKey = "$parent";
    internal const string ChildKey = "$child";
    internal const string BaseKey = "$base";
    internal const string FindAllKey = "$findAll";
    internal const string AssertKey = "$assert";

    /// <summary>How a <c>$findAll</c> operand reads a path of the record a lookup finds: <c>$lookup.wellbore.GeoContexts.FieldID</c>.</summary>
    internal const string LookupReference = Marker + MappingLookup.Prefix;

    /// <summary>How a column of the dataset's own row is named from anywhere in the tree: <c>$dataset.log_id</c>.</summary>
    internal const string DatasetReference = Marker + DatasetColumn.Prefix;

    /// <summary>How a replace names a table read from the partition's cache: <c>$cache.UnitAlias</c>.</summary>
    internal const string CacheReference = Marker + MappingSource.CachePrefix;

    /// <summary>The keys a value node reads its value with; naming one of them makes a map a value node.</summary>
    internal static readonly IReadOnlyList<string> SourceKeys = [FromKey, ExprKey, ValueKey, CacheKey, SearchKey, LookupKey, ParentKey, ChildKey, BaseKey, CoalesceKey];

    /// <summary>The settings a value node takes beside the key it reads its value with.</summary>
    internal static readonly IReadOnlyList<string> ValueSettings = [FindByKey, FindAllKey, ModifiersKey, WhenKey, RequiredKey, IgnoreSeparatorsKey, UnverifiedKey, AssertKey, DescriptionKey];

    /// <summary>The settings a <c>$lookup</c> node takes beside it: the lookup says how its record is found.</summary>
    internal static readonly IReadOnlyList<string> LookupSettings = [WhenKey, RequiredKey, AssertKey, DescriptionKey];

    /// <summary>The keys a lookup of the <c>lookups</c> block is written with.</summary>
    internal static readonly IReadOnlyList<string> LookupKeys = [CacheKey, FindByKey, ModifiersKey, IgnoreSeparatorsKey, DescriptionKey];

    /// <summary>The settings a <c>$coalesce</c> node takes beside its alternatives: those that decide for all of them.</summary>
    internal static readonly IReadOnlyList<string> CoalesceSettings = [WhenKey, RequiredKey, AssertKey, DescriptionKey];

    /// <summary>The settings a <c>$forEach</c> node takes beside the child dataset it repeats.</summary>
    internal static readonly IReadOnlyList<string> RepeatSettings = [ItemKey, WhereKey, WhenKey, RequiredKey, AssertKey, DescriptionKey];

    /// <summary>Every word of the mapping language a key of the record tree can be.</summary>
    internal static readonly IReadOnlyList<string> NodeKeys =
        [FromKey, ExprKey, ValueKey, CacheKey, SearchKey, LookupKey, ParentKey, ChildKey, BaseKey, CoalesceKey, ForEachKey, ItemKey, WhereKey, FindByKey, FindAllKey, ModifiersKey, WhenKey, RequiredKey, IgnoreSeparatorsKey, UnverifiedKey, AssertKey, DescriptionKey];

    /// <summary>The value keys, as a message lists them.</summary>
    private static string SourceList => string.Join(", ", SourceKeys.Take(SourceKeys.Count - 1)) + " or " + SourceKeys[^1];

    /// <summary>
    /// What a column a node names reads: the dataset's own row, or the item row of the <c>$forEach</c> the node is under;
    /// the mapping's lookups, which a node anywhere in the tree reads by name; and the list of objects whose item the node
    /// is in, if any.
    /// </summary>
    private sealed record TreeScope(string? Child)
    {
        private static readonly IReadOnlyDictionary<string, MappingLookup> NoLookups = new Dictionary<string, MappingLookup>(StringComparer.Ordinal);

        public static TreeScope Root { get; } = new((string?)null);

        public IReadOnlyDictionary<string, MappingLookup> Lookups { get; init; } = NoLookups;

        /// <summary>Where the document writes the list of objects whose item the node is in, or null outside one.</summary>
        public string? List { get; init; }

        public string Rows => Child is null ? "the dataset's own row" : $"the rows of {Child}, which the enclosing {ForEachKey} repeats";
    }

    /// <summary>True for a key that is a word of the mapping language: it starts with <c>$</c>, and not with <c>$$</c>.</summary>
    internal static bool IsNodeKey(string key) => key.StartsWith(Marker, StringComparison.Ordinal) && !key.StartsWith(Marker + Marker, StringComparison.Ordinal);

    /// <summary>The record property a key of the tree names: the key itself, or for <c>$$name</c> the name <c>$name</c>.</summary>
    internal static string PropertyOfKey(string key) => key.StartsWith(Marker + Marker, StringComparison.Ordinal) ? key[1..] : key;

    /// <summary>A record property as a key of the tree writes it: a name that starts with <c>$</c> takes one more.</summary>
    internal static string KeyOfProperty(string property) => property.StartsWith(Marker, StringComparison.Ordinal) ? Marker + property : property;

    /// <summary>Reads the record block into one entry per variable, in document order.</summary>
    private static List<MappingEntry> ReadRecord(Dictionary<string, object?>? record, IReadOnlyDictionary<string, MappingLookup> lookups, string source)
    {
        if (record is null || record.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: 'record' lays out the record the mapping renders, with its acl, legal and data, such as record: {{ data: {{ Name: {{ {FromKey}: name }} }} }}.");
        }

        if (record.Keys.FirstOrDefault(IsNodeKey) is { } word)
        {
            throw new FlowValidationException(
                $"{source}: record holds '{word}', and record itself is the object the mapping renders: its keys are the record's properties (acl, legal, tags, data), and each of them holds a node.");
        }

        var root = TreeScope.Root with { Lookups = lookups };
        var entries = new List<MappingEntry>();
        foreach (var (key, node) in record)
        {
            var location = "record." + key;
            Node(node, [PropertyName(key, $"{source}: {location}")], location, root, entries, source);
        }

        return entries;
    }

    /// <summary>
    /// The <c>lookups</c> block: each lookup names the cached type its record is found in (<c>$cache: Wellbore</c>) and the
    /// findBy lines that find it, read against the dataset's own row, with the modifiers and the separator fold a cache
    /// node takes. A lookup nothing reads is refused, since it says something no record uses.
    /// </summary>
    private static Dictionary<string, MappingLookup> ReadLookups(Dictionary<string, object?>? written, string source)
    {
        var lookups = new Dictionary<string, MappingLookup>(StringComparer.Ordinal);
        const string Example = $"lookups: {{ wellbore: {{ {CacheKey}: Wellbore, {FindByKey}: FacilityName = wellbore_uwi }} }}";
        foreach (var (name, node) in written ?? [])
        {
            var at = $"{source}: lookups.{name}";
            if (!NamePattern().IsMatch(name))
            {
                throw new FlowValidationException($"{at}: a lookup is named by letters, digits, underscores and hyphens, such as {Example}.");
            }

            if (node is not IDictionary<object, object> { Count: > 0 } map)
            {
                throw new FlowValidationException($"{at} names the cached type its record is found in and how, such as {Example}.");
            }

            foreach (var key in map.Keys.Select(KeyText).Where(key => !LookupKeys.Contains(key)))
            {
                throw new FlowValidationException(key == WhenKey || key == RequiredKey
                    ? $"{at}: {key} decides for the node that reads a lookup, not for the lookup; write it beside {LookupKey} where the record reads it."
                    : $"{at}: a lookup takes {SettingList(LookupKeys)}, not '{key}'.");
            }

            var type = RequiredText(map, CacheKey, at);
            if (!NamePattern().IsMatch(type))
            {
                throw new FlowValidationException(
                    $"{at}: {CacheKey} names the cached type the record is found in, such as {CacheKey}: Wellbore; '{type}' is not one. The field is read where the record reads the lookup, with {LookupKey}: {name}.<field>.");
            }

            var read = new MappingSource { Kind = MappingSourceKind.Cache, CacheType = type, CacheField = "id" };
            var findBy = FindByLines(Get(map, FindByKey), read, TreeScope.Root, at);
            if (findBy.Count == 0)
            {
                throw new FlowValidationException($"{at}: a lookup needs {FindByKey}, which says how the row finds its record, such as {FindByKey}: FacilityName = wellbore_uwi.");
            }

            var modifiers = Modifiers(Get(map, ModifiersKey), Has(map, ModifiersKey), TreeScope.Root, at);
            if (modifiers.Count > 0 && findBy.All(f => f.Column is null))
            {
                throw new FlowValidationException($"{at}: {ModifiersKey} change incoming dataset values, and this lookup's {FindByKey} reads none; cache values are never modified.");
            }

            if (modifiers.FirstOrDefault(m => m.BuildsId) is { } builder)
            {
                throw new FlowValidationException(
                    $"{at}: the {Word(builder)} modifier builds an id to write, and a lookup finds a record in the cache; the record's id is read with {LookupKey}: {name}.id.");
            }

            lookups[name] = new MappingLookup
            {
                Name = name,
                CacheType = type,
                FindBy = findBy,
                Modifiers = modifiers,
                IgnoreSeparators = Flag(map, IgnoreSeparatorsKey, at) ?? false,
                Description = Text(map, DescriptionKey, at),
            };
        }

        return lookups;
    }

    private static void Node(object? node, IReadOnlyList<string> path, string location, TreeScope scope, List<MappingEntry> entries, string source)
    {
        var at = $"{source}: {location}";
        switch (node)
        {
            case null:
                throw new FlowValidationException($"{at} has no value; give it one, or remove it.");

            case IDictionary<object, object> map when map.Count == 0:
                throw new FlowValidationException($"{at} is an empty object; name the properties it holds, or write {{ {ValueKey}: {{}} }} for an empty object.");

            case IDictionary<object, object> map when IsNode(map, at):
                if (Has(map, ForEachKey))
                {
                    Repeat(map, path, location, scope, entries, source);
                    return;
                }

                entries.Add(Value(map, path, location, scope, entries.Count, source));
                return;

            case IDictionary<object, object> map:
                foreach (var (key, child) in map)
                {
                    var name = KeyText(key);
                    Node(child, [.. path, PropertyName(name, at)], location + "." + name, scope, entries, source);
                }

                return;

            case IEnumerable<object> list:
                if (MappedItem(list, string.Empty) is not null)
                {
                    var items = list.ToList();
                    entries.Add(items.Any(IsObjectItem)
                        ? ListOfObjects(items, path, location, scope, entries.Count, source)
                        : ListOfValues(items, path, location, scope, entries.Count, source));
                    return;
                }

                entries.Add(Literal(TreeLiteral(list, at), path, location, entries.Count, source));
                return;

            default:
                entries.Add(Literal(TreeLiteral(node, at), path, location, entries.Count, source));
                return;
        }
    }

    /// <summary>
    /// Whether a map is a node of the mapping language: it holds a key that starts with <c>$</c>. A node's keys all do, so a
    /// map mixing them with record properties is refused, as is a word the language does not have.
    /// </summary>
    private static bool IsNode(IDictionary<object, object> map, string at)
    {
        var keys = map.Keys.Select(KeyText).ToList();
        var words = keys.Where(IsNodeKey).ToList();
        if (words.Count == 0)
        {
            return false;
        }

        if (keys.FirstOrDefault(k => !IsNodeKey(k)) is { } property)
        {
            throw new FlowValidationException(
                $"{at} holds {string.Join(" and ", words.Take(2).Select(w => $"'{w}'"))}, words of the mapping language, beside the property '{property}'; a node's keys all start with '{Marker}'. "
                + $"Lay the properties out under a key of their own, or write a property whose name starts with '{Marker}' as {Marker}{Marker}<name>.");
        }

        foreach (var word in words.Where(w => !NodeKeys.Contains(w)))
        {
            throw new FlowValidationException(Nearest(word, NodeKeys) is { } nearest
                ? $"{at}: '{word}' is not a word of the mapping language. Did you mean '{nearest}'? A property whose name starts with '{Marker}' is written {Marker}{Marker}{word[1..]}."
                : $"{at}: '{word}' is not a word of the mapping language, which reads {SourceList} with their settings, or {ForEachKey} and {ItemKey}. A property whose name starts with '{Marker}' is written {Marker}{Marker}{word[1..]}.");
        }

        if (!Has(map, ForEachKey) && !SourceKeys.Any(key => Has(map, key)))
        {
            throw new FlowValidationException(Has(map, ItemKey)
                ? $"{at} lays out {ItemKey}, the items of an array whose rows a {ForEachKey} node repeats; add {ForEachKey}: <child dataset>."
                : $"{at} reads no value: a node reads one with {SourceList}, or repeats the rows of a child dataset with {ForEachKey}, and takes its settings ({string.Join(", ", words)}) beside it.");
        }

        return true;
    }

    private static MappingEntry Literal(JsonNode node, IReadOnlyList<string> path, string location, int index, string source)
        => new()
        {
            Index = index,
            Location = location,
            Target = Target(path, $"{source}: {location}"),
            Static = node,
        };

    /// <summary>
    /// A list of values some of whose items are value nodes: each item is a literal value or a node that reads one value or,
    /// with <c>$findAll</c>, many, and the list is what they give in the order they are written, a value given twice
    /// written once. An item is never a list of its own, and a list with an object among its items is a list of objects
    /// (<see cref="ListOfObjects"/>); a list of objects whose items come from rows is a <c>$forEach</c> node.
    /// </summary>
    private static MappingEntry ListOfValues(List<object> list, IReadOnlyList<string> path, string location, TreeScope scope, int index, string source)
    {
        var at = $"{source}: {location}";
        var target = Target(path, at);
        var parts = new List<MappingEntry>(list.Count);
        var literals = new JsonArray();
        for (var i = 0; i < list.Count; i++)
        {
            var itemLocation = $"{location}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var itemAt = $"{source}: {itemLocation}";
            switch (list[i])
            {
                case null:
                    throw new FlowValidationException($"{itemAt} is empty; give the item a value, or remove it.");

                case IDictionary<object, object> map when map.Keys.Select(KeyText).Any(IsNodeKey):
                    _ = IsNode(map, itemAt);
                    if (Has(map, ForEachKey) || Has(map, ItemKey))
                    {
                        throw new FlowValidationException(
                            $"{itemAt} repeats rows with {ForEachKey}, and an item of a list of values reads values; an array of objects whose items come from rows is a {ForEachKey} node of its own.");
                    }

                    if (Has(map, CoalesceKey))
                    {
                        throw new FlowValidationException(
                            $"{itemAt} takes the first of its alternatives with {CoalesceKey}, and an item of a list gives what it reads; write each alternative as an item of its own, since the list keeps every value its items give.");
                    }

                    if (Has(map, AssertKey))
                    {
                        throw new FlowValidationException(
                            $"{itemAt}: {AssertKey} judges the value of a property, and this item gives one of the values of the list at {location}, which has no node of its own to assert on; a list of values takes no assertions.");
                    }

                    var part = Value(map, path, itemLocation, scope, index, source);
                    if (part.IsPlainLiteral)
                    {
                        // A $value without a condition is a literal every record carries, like a bare item.
                        literals.Add(part.Static!.DeepClone());
                    }

                    parts.Add(part);
                    break;

                case IDictionary<object, object> or IEnumerable<object>:
                    // An item that is an object makes the list a list of objects, so only a list inside the list reaches here.
                    throw new FlowValidationException(
                        $"{itemAt} is a list, and an item of a list is a value or an object, never a list of its own: a list of values holds literal values and nodes that read values.");

                default:
                    var literal = TreeLiteral(list[i]!, itemAt);
                    literals.Add(literal.DeepClone());
                    parts.Add(new MappingEntry { Index = index, Location = itemLocation, Target = target, Static = literal });
                    break;
            }
        }

        return new MappingEntry
        {
            Index = index,
            Location = location,
            Target = target,
            Static = literals,
            Parts = parts,
        };
    }

    /// <summary>An item of a list that is an object laid out as the record tree: a map whose keys are properties, not words of the mapping language.</summary>
    private static bool IsObjectItem(object? item) => item is IDictionary<object, object> map && !map.Keys.Select(KeyText).Any(IsNodeKey);

    /// <summary>
    /// A list of objects some of whose properties read values: each item is an object laid out as the record tree lays out
    /// an object's properties (literals, value nodes, <c>$coalesce</c> nodes, objects, lists of values), filling the
    /// variables of the list's items (<c>osdu.data.TechnicalAssurances[].TechnicalAssuranceTypeID</c>) and reading the row
    /// the list is in. The list is the objects its items give, in the order they are written, an object given twice written
    /// once; an item none of whose properties gives a value adds nothing, and a property that holds holds the record. An
    /// item written as the literal it is is carried by every record. A list is one of objects or one of values, since the
    /// variable it fills takes one or the other, and a path steps into one array at most, so an item's properties never
    /// repeat rows and a list of objects is never inside the items of another array.
    /// </summary>
    private static MappingEntry ListOfObjects(List<object> list, IReadOnlyList<string> path, string location, TreeScope scope, int index, string source)
    {
        var at = $"{source}: {location}";
        var target = Target(path, at);
        if (target.Repeater is { } outer)
        {
            throw new FlowValidationException(
                $"{at} is a list of objects inside the items of {outer.Text}, and a list of objects inside the items of another array is not supported.");
        }

        var example = $"- {{ TechnicalAssuranceTypeID: {{ {ExprKey}: iif(startsWith(log_source, \"COMP\"), \"Certified\", \"Unevaluated\") }} }}";
        var first = list.FindIndex(IsObjectItem);
        var itemPath = path.Take(path.Count - 1).Append(path[^1] + "[]").ToList();
        var itemScope = scope with { List = location };
        var parts = new List<MappingEntry>(list.Count);
        var literals = new JsonArray();
        for (var i = 0; i < list.Count; i++)
        {
            var itemLocation = $"{location}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var itemAt = $"{source}: {itemLocation}";
            switch (list[i])
            {
                case null:
                    throw new FlowValidationException($"{itemAt} is empty; give the item the properties it holds, or remove it.");

                case IDictionary<object, object> { Count: 0 }:
                    throw new FlowValidationException($"{itemAt} is an empty object; name the properties the item holds, or remove it.");

                case IDictionary<object, object> map when IsObjectItem(map):
                    if (MappedItem(map, string.Empty) is null)
                    {
                        // An item written as the literal it is: every record carries it, as it carries the items of a literal list.
                        var literal = TreeLiteral(map, itemAt);
                        literals.Add(literal.DeepClone());
                        parts.Add(new MappingEntry { Index = index, Location = itemLocation, Target = target, Static = literal });
                        break;
                    }

                    var properties = new List<MappingEntry>(map.Count);
                    foreach (var (key, child) in map)
                    {
                        var name = KeyText(key);
                        Node(child, [.. itemPath, PropertyName(name, itemAt)], $"{itemLocation}.{name}", itemScope, properties, source);
                    }

                    parts.Add(new MappingEntry { Index = index, Location = itemLocation, Target = target, Properties = properties });
                    break;

                case IDictionary<object, object>:
                    throw new FlowValidationException(
                        $"{itemAt} is a node of the mapping language, and the items of a list of objects are objects laid out as the record tree, whose keys are the item's properties, such as {example}. "
                        + "A condition goes on the properties it decides for; an item none of whose properties gives a value adds nothing to the list.");

                default:
                    throw new FlowValidationException(
                        $"{itemAt} is {(list[i] is IEnumerable<object> ? "a list" : "a value")}, and {location}[{first.ToString(CultureInfo.InvariantCulture)}] is an object: the items of a list are all objects, or all values.");
            }
        }

        return new MappingEntry
        {
            Index = index,
            Location = location,
            Target = target,
            Static = literals,
            Parts = parts,
        };
    }

    /// <summary>
    /// A <c>$forEach</c> node: an entry that repeats the child dataset's rows as the array's items, then the entries of the
    /// item, each reading the item's row. Its own <c>$when</c> and <c>$required</c> read the row the node is in, since they
    /// decide for the whole array; its <c>$where</c> reads each child row, and keeps the rows it holds for.
    /// </summary>
    private static void Repeat(IDictionary<object, object> map, IReadOnlyList<string> path, string location, TreeScope scope, List<MappingEntry> entries, string source)
    {
        var at = $"{source}: {location}";
        foreach (var key in map.Keys.Select(KeyText))
        {
            if (key == ForEachKey || RepeatSettings.Contains(key))
            {
                continue;
            }

            throw new FlowValidationException(SourceKeys.Contains(key)
                ? $"{at} repeats rows with {ForEachKey} and reads a value with {key}; a node does one of them. What each row gives is a property under {ItemKey}."
                : $"{at}: a {ForEachKey} node takes {ForEachKey}, {SettingList(RepeatSettings)}, not '{key}'.");
        }

        if (scope.Child is not null)
        {
            throw new FlowValidationException(
                $"{at} repeats rows inside the items of the {ForEachKey} over {scope.Child}; a repeated array inside a repeated item is not supported.");
        }

        if (scope.List is not null)
        {
            throw new FlowValidationException(
                $"{at} repeats rows inside an item of the list of objects at {scope.List}; a repeated array inside the item of a list is not supported.");
        }

        var child = Get(map, ForEachKey) is { } named and not (IDictionary<object, object> or IEnumerable<object>) ? ScalarText(named).Trim() : string.Empty;
        if (!NamePattern().IsMatch(child))
        {
            throw new FlowValidationException(
                $"{at}: {ForEachKey} names the child dataset whose rows become the array's items, as the flow declares it under source.datasets, such as {ForEachKey}: curves; '{child}' is not one.");
        }

        var repeatRequired = Flag(map, RequiredKey, at) ?? true;
        entries.Add(new MappingEntry
        {
            Index = entries.Count,
            Location = location,
            Target = Target(path, at),
            Source = new MappingSource { Kind = MappingSourceKind.DatasetRows, Child = child },
            AppliesWhen = Has(map, WhenKey) ? Condition(Get(map, WhenKey), WhenKey, scope, at) : null,
            RowFilter = Has(map, WhereKey) ? Condition(Get(map, WhereKey), WhereKey, scope with { Child = child }, at) : null,
            Required = repeatRequired,
            Assertions = Assertions(map, $"a {ForEachKey} node writes an array of the rows of {child}", scope, repeatRequired, location, source),
            Description = Text(map, DescriptionKey, at),
        });

        const string ItemExample = $"{ItemKey}: {{ CurveID: {{ {FromKey}: curve_id }} }}";
        var item = Get(map, ItemKey) switch
        {
            IDictionary<object, object> { Count: > 0 } properties when !properties.Keys.Select(KeyText).Any(IsNodeKey) => properties,
            IDictionary<object, object> { Count: > 0 } properties when properties.Keys.Select(KeyText).All(IsNodeKey) => throw new FlowValidationException(
                $"{at}.{ItemKey} is a node that reads one value, and the items of a repeated array are objects here: {ItemKey} names each property a row fills, such as {ItemExample}. "
                + "An array of values from rows is not supported yet."),
            IDictionary<object, object> { Count: > 0 } properties => throw new FlowValidationException(
                $"{at}.{ItemKey} holds '{properties.Keys.Select(KeyText).First(IsNodeKey)}', a word of the mapping language, beside properties of the item; {ItemKey} names each property a row fills, such as {ItemExample}."),
            null when !Has(map, ItemKey) => throw new FlowValidationException(
                $"{at} repeats the rows of {child} and lays out no item; {ItemKey} names each property a row fills, such as {ItemExample}."),
            _ => throw new FlowValidationException($"{at}.{ItemKey} names each property a row fills, such as {ItemExample}."),
        };

        var itemScope = scope with { Child = child };
        var itemPath = path.Take(path.Count - 1).Append(path[^1] + "[]").ToList();
        foreach (var (key, node) in item)
        {
            var name = KeyText(key);
            var itemLocation = $"{location}.{ItemKey}.{name}";
            Node(node, [.. itemPath, PropertyName(name, $"{source}: {location}.{ItemKey}")], itemLocation, itemScope, entries, source);
        }
    }

    /// <summary>A value node: one value read from a column, a literal, the cache or a search, and the settings beside it.</summary>
    private static MappingEntry Value(IDictionary<object, object> map, IReadOnlyList<string> path, string location, TreeScope scope, int index, string source)
    {
        var at = $"{source}: {location}";
        foreach (var key in map.Keys.Select(KeyText))
        {
            if (SourceKeys.Contains(key) || ValueSettings.Contains(key))
            {
                continue;
            }

            throw new FlowValidationException(key == ItemKey
                ? $"{at} names {ItemKey}, which lays out the items of a {ForEachKey} node; add {ForEachKey}: <child dataset> and leave out {string.Join(" and ", SourceKeys.Where(k => Has(map, k)))}, or remove {ItemKey}."
                : $"{at}: a value node takes {SourceList}, then {SettingList(ValueSettings)}, not '{key}'.");
        }

        var named = SourceKeys.Where(key => Has(map, key)).ToList();
        if (named.Count > 1)
        {
            throw new FlowValidationException($"{at} reads its value with {string.Join(" and ", named)}; a node reads one of {SourceList}.");
        }

        if (named[0] == CoalesceKey)
        {
            return Coalesce(map, path, location, scope, index, source);
        }

        var target = Target(path, at);
        var description = Text(map, DescriptionKey, at);
        var condition = Has(map, WhenKey) ? Condition(Get(map, WhenKey), WhenKey, scope, at) : null;

        if (named[0] == ValueKey)
        {
            if (Has(map, AssertKey))
            {
                throw new FlowValidationException(
                    $"{at}: {AssertKey} judges the value a record is given, and a literal {ValueKey} gives every record the same one; remove {AssertKey}, or read the value from the row with {FromKey} or {ExprKey}.");
            }

            if (new[] { FindByKey, FindAllKey, ModifiersKey, RequiredKey, IgnoreSeparatorsKey, UnverifiedKey }.Any(key => Has(map, key)))
            {
                throw new FlowValidationException(
                    $"{at}: a literal {ValueKey} takes only {WhenKey} and {DescriptionKey} beside it; {FindByKey}, {FindAllKey}, {ModifiersKey}, {RequiredKey}, {IgnoreSeparatorsKey} and {UnverifiedKey} belong to a node that reads the dataset, the cache or a search.");
            }

            var literal = StaticValue(Get(map, ValueKey), at)
                ?? throw new FlowValidationException($"{at}: {ValueKey} is empty; leave the property out by removing it.");
            CheckLiteralTexts(literal, at);
            return new MappingEntry
            {
                Index = index,
                Location = location,
                Target = target,
                Static = literal,
                AppliesWhen = condition,
                Description = description,
            };
        }

        if (named[0] == LookupKey)
        {
            return LookupNode(map, target, location, scope, index, condition, description, source);
        }

        var read = named[0] switch
        {
            FromKey => new MappingSource { Kind = MappingSourceKind.DatasetColumn, Column = Column(RequiredText(map, FromKey, at), scope, $"{at}: {FromKey}") },
            ExprKey => new MappingSource { Kind = MappingSourceKind.Expression, Expression = Expression(Get(map, ExprKey), ExprKey, scope, at) },
            CacheKey => CacheSource(RequiredText(map, CacheKey, at), at),
            ParentKey => ParentSource(RequiredText(map, ParentKey, at), scope, at),
            ChildKey => ChildSource(RequiredText(map, ChildKey, at), scope, at),
            BaseKey => BaseSource(RequiredText(map, BaseKey, at), scope, at),
            _ => SearchSource(RequiredText(map, SearchKey, at), at),
        };

        if (!read.Resolves && Has(map, FindByKey))
        {
            throw new FlowValidationException(read.Kind switch
            {
                MappingSourceKind.Expression => $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node computes its value with {ExprKey}.",
                MappingSourceKind.Parent => $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node reads its record's parent, which is found by the record's own id.",
                MappingSourceKind.Child => $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node links one of its record's children, which is found by the record's own id.",
                MappingSourceKind.Base => $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node reads the record the fragment adds to, which is the one being rendered.",
                _ => $"{at}: {FindByKey} selects the record a {CacheKey} or a {SearchKey} node reads; this node reads the column {Written(read.Column!, scope)}.",
            });
        }

        if (read.Kind != MappingSourceKind.Cache && Has(map, IgnoreSeparatorsKey))
        {
            throw new FlowValidationException($"{at}: {IgnoreSeparatorsKey} loosens how a value is matched against the cache, so it only applies to a {CacheKey} node.");
        }

        FindAllQuery? findAll = null;
        if (Has(map, FindAllKey))
        {
            if (read.Kind != MappingSourceKind.Cache)
            {
                throw new FlowValidationException(
                    $"{at}: {FindAllKey} reads every row of a cached type that matches, so it belongs to a {CacheKey} node; this node reads {read}.");
            }

            if (Has(map, FindByKey))
            {
                throw new FlowValidationException(
                    $"{at} finds its record with {FindByKey} and reads every matching row with {FindAllKey}; a node does one of them: {FindByKey} reads one record, {FindAllKey} a list from all of them.");
            }

            if (Has(map, IgnoreSeparatorsKey))
            {
                throw new FlowValidationException(
                    $"{at}: {IgnoreSeparatorsKey} loosens how one record is found by name, and {FindAllKey} reads every row whose key holds the value, ignoring case only; remove {IgnoreSeparatorsKey}.");
            }

            findAll = FindAllLines(Get(map, FindAllKey), read, scope, at);
        }

        if (read.Kind == MappingSourceKind.Child)
        {
            // The reference is OSDU's own id of the child, so there is nothing a modifier could change into another link; and a
            // child is never a reason to hold its parent: a parent waiting for a child that reads it would wait for ever.
            if (Has(map, ModifiersKey))
            {
                throw new FlowValidationException(
                    $"{at}: {ModifiersKey} change a value the row gives, and a {ChildKey} node writes the reference to the record's child as OSDU names it, with the version separator and no version; remove {ModifiersKey}.");
            }

            if (Flag(map, RequiredKey, at) == true)
            {
                throw new FlowValidationException(
                    $"{at}: a {ChildKey} node never holds its record: the link is written once OSDU holds the child, and left out until then, since a parent waiting for a child that waits for its parent would wait for ever; remove {RequiredKey}.");
            }
        }

        var findBy = read.Resolves && findAll is null ? FindByLines(Get(map, FindByKey), read, scope, at) : [];
        if (read.Resolves && findBy.Count == 0 && findAll is null)
        {
            throw new FlowValidationException(read.Kind == MappingSourceKind.Cache
                ? $"{at}: a {CacheKey} node needs {FindByKey}, which says which record to read, such as {FindByKey}: {FindByExample(read.Prefix)}, or {FindAllKey}, which reads every row that matches, such as {FindAllKey}: FieldIDList = $lookup.wellbore.GeoContexts.FieldID."
                : $"{at}: a {Marker}{read.Prefix} node needs {FindByKey}, which says which record to read, such as {FindByKey}: {FindByExample(read.Prefix)}.");
        }

        var modifiers = Modifiers(Get(map, ModifiersKey), Has(map, ModifiersKey), scope, at);
        var comparesColumn = findAll is not null ? findAll.Operand.Column is not null : findBy.Any(f => f.Column is not null);
        if (modifiers.Count > 0 && read.Resolves && !comparesColumn)
        {
            var found = read.Kind == MappingSourceKind.Search ? "what a search finds is" : "cache values are";
            throw new FlowValidationException($"{at}: {ModifiersKey} change incoming dataset values, and this node's {(findAll is null ? FindByKey : FindAllKey)} reads none; {found} never modified.");
        }

        // id and ref both build the id a node writes, so the same rules hold for either.
        var builders = modifiers.Where(m => m.BuildsId).ToList();
        if (builders.Count > 0 && read.Resolves)
        {
            // A resolved node's modifiers change the value its findBy lines compare, and the node gives the record's id itself;
            // an id built there would be compared with the cached or searched field, never written.
            throw new FlowValidationException(
                $"{at}: the {Word(builders[0])} modifier builds the id a node writes from a dataset value, and a {Marker}{read.Prefix} node already gives what it writes; read the value with {FromKey}: <column>, and build the id from it.");
        }

        if (builders.Count > 1)
        {
            throw new FlowValidationException(
                $"{at}: a node builds one id, and its {ModifiersKey} list {string.Join(" and ", builders.Select(Word))}; keep one.");
        }

        if (builders.Count == 1 && !modifiers[^1].BuildsId)
        {
            // A modifier after the id would change the id it built, into one its template does not describe.
            throw new FlowValidationException($"{at}: {Word(builders[0])} builds what the node writes, so it is the last modifier; move {modifiers[^1]} before it.");
        }

        var unverified = Flag(map, UnverifiedKey, at) ?? false;
        if (unverified && builders.Count == 0)
        {
            throw new FlowValidationException(
                $"{at}: {UnverifiedKey} lets an id the node builds with id or ref go out when the cache holds no record under it, and this node builds no id; build one, or remove {UnverifiedKey}.");
        }

        var required = Flag(map, RequiredKey, at) ?? read.Kind != MappingSourceKind.Child;
        var notIncoming = read.ReadsRow
            ? null
            : read.Kind switch
            {
                MappingSourceKind.Search => "this node's value is the id a search of the platform finds",
                MappingSourceKind.Parent => "this node's value is read from the record's parent",
                MappingSourceKind.Child => "this node's value is the reference to one of the record's children",
                MappingSourceKind.Base => "this node's value is read from the record the fragment adds to",
                _ => "this node's value comes from the partition's cache",
            };
        return new MappingEntry
        {
            Index = index,
            Location = location,
            Target = target,
            Source = read,
            FindBy = findBy,
            FindAll = findAll,
            Modifiers = modifiers,
            AppliesWhen = condition,
            Required = required,
            IgnoreSeparators = Flag(map, IgnoreSeparatorsKey, at) ?? false,
            Unverified = unverified,
            Assertions = Assertions(map, notIncoming, scope, required, location, source),
            Description = description,
        };
    }

    /// <summary>
    /// A <c>$lookup</c> node: <c>$lookup: wellbore.id</c> reads a field of the record the lookup finds, which is the node
    /// the lookup's type and findBy lines would be as a <c>$cache</c> node of its own. It takes <c>$when</c>,
    /// <c>$required</c> and <c>$description</c>; how the record is found is the lookup's to say.
    /// </summary>
    private static MappingEntry LookupNode(
        IDictionary<object, object> map, TemplatePath target, string location, TreeScope scope, int index, MappingExpression? condition, string? description, string source)
    {
        var at = $"{source}: {location}";
        foreach (var key in map.Keys.Select(KeyText).Where(key => key != LookupKey && !LookupSettings.Contains(key)))
        {
            throw new FlowValidationException(key is FindByKey or FindAllKey or ModifiersKey or IgnoreSeparatorsKey
                ? $"{at}: {key} says how a record is found, and a {LookupKey} node reads the record its lookup finds; write {key} on the lookup under lookups."
                : $"{at}: a {LookupKey} node takes {SettingList(LookupSettings)} beside it, not '{key}'.");
        }

        var (lookup, path) = LookupPath(RequiredText(map, LookupKey, at), scope, $"{at}: {LookupKey}", LookupKey + ": wellbore.id");
        var required = Flag(map, RequiredKey, at) ?? true;
        return new MappingEntry
        {
            Index = index,
            Location = location,
            Target = target,
            Source = new MappingSource { Kind = MappingSourceKind.Cache, CacheType = lookup.CacheType, CacheField = path, Lookup = lookup.Name },
            FindBy = lookup.FindBy,
            Modifiers = lookup.Modifiers,
            IgnoreSeparators = lookup.IgnoreSeparators,
            AppliesWhen = condition,
            Required = required,
            Assertions = Assertions(map, $"this node's value is a field of the record lookup '{lookup.Name}' finds", scope, required, location, source),
            Description = description,
        };
    }

    /// <summary>
    /// A lookup and a path of its record, written <c>&lt;lookup&gt;.&lt;field&gt;</c>: <c>wellbore.id</c>,
    /// <c>wellbore.GeoContexts.FieldID</c>. The lookup is one the <c>lookups</c> block declares.
    /// </summary>
    private static (MappingLookup Lookup, string Path) LookupPath(string text, TreeScope scope, string at, string example)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length < 2 || !NamePattern().IsMatch(parts[0]) || parts.Skip(1).Any(p => !FieldPattern().IsMatch(p)))
        {
            throw new FlowValidationException($"{at} names a lookup and the field of its record it reads, such as {example}; '{text}' is not one.");
        }

        if (!scope.Lookups.TryGetValue(parts[0], out var lookup))
        {
            throw new FlowValidationException(scope.Lookups.Count == 0
                ? $"{at} reads lookup '{parts[0]}', and the mapping declares no lookups; declare it under lookups, such as lookups: {{ {parts[0]}: {{ {CacheKey}: Wellbore, {FindByKey}: FacilityName = wellbore_uwi }} }}."
                : $"{at} reads lookup '{parts[0]}', which the mapping does not declare; it declares {string.Join(", ", scope.Lookups.Keys.Order(StringComparer.Ordinal))}.");
        }

        return (lookup, string.Join('.', parts.Skip(1)));
    }

    /// <summary>
    /// A cache node's <c>$findAll</c>: one line or a list of them, which a row must hold every one of. Exactly one line is
    /// the key, <c>&lt;field&gt; = &lt;operand&gt;</c>, comparing a field of the row with a column, a quoted text or a path
    /// of the record a lookup finds (<c>$lookup.wellbore.GeoContexts.FieldID</c>); every other line is
    /// <c>&lt;field&gt; is empty</c>, which a row holding nothing under that field meets.
    /// </summary>
    private static FindAllQuery FindAllLines(object? value, MappingSource read, TreeScope scope, string at)
    {
        const string Example = "FieldIDList = $lookup.wellbore.GeoContexts.FieldID";
        List<string> lines = value switch
        {
            string one => [one],
            IEnumerable<object> many => many.Select(line => line as string
                ?? throw new FlowValidationException($"{at}: each {FindAllKey} line is text, such as {Example} or FieldList is empty.")).ToList(),
            _ => throw new FlowValidationException($"{at}: {FindAllKey} is one line or a list of lines, such as {FindAllKey}: {Example}."),
        };

        (string Field, FindAllOperand Operand)? key = null;
        var empty = new List<string>();
        foreach (var line in lines)
        {
            if (EmptyLinePattern().Match(line) is { Success: true } emptiness)
            {
                var emptyField = CachedField(emptiness.Groups["field"].Value, line, at);
                if (empty.Contains(emptyField, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException($"{at}: {FindAllKey} '{line}' says it a second time.");
                }

                empty.Add(emptyField);
                continue;
            }

            var match = FindByPattern().Match(line);
            if (!match.Success)
            {
                throw new FlowValidationException(
                    $"{at}: {FindAllKey} '{line}' must read <field> = <column>, <field> = 'text', <field> = {LookupReference}.<lookup>.<field>, or <field> is empty, such as {Example}.");
            }

            if (key is not null)
            {
                throw new FlowValidationException(
                    $"{at}: {FindAllKey} compares a field with a value on '{line}' and on '{key.Value.Field} = {key.Value.Operand}'; it takes one such line, the key the rows are found by, and any number of <field> is empty.");
            }

            var field = CachedField(match.Groups["field"].Value, line, at);
            var operand = match.Groups["operand"].Value.Trim();
            if (Quoted(operand) is { } literal)
            {
                if (literal.Length == 0)
                {
                    throw new FlowValidationException($"{at}: {FindAllKey} '{line}' compares with empty text.");
                }

                key = (field, new FindAllOperand(null, literal, null, null));
            }
            else if (operand.StartsWith(LookupReference + ".", StringComparison.Ordinal))
            {
                var (lookup, path) = LookupPath(operand[(LookupReference.Length + 1)..], scope, $"{at}: {FindAllKey} '{line}'", LookupReference + ".wellbore.GeoContexts.FieldID");
                if (string.Equals(lookup.CacheType, read.CacheType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{at}: {FindAllKey} '{line}' finds {read.CacheType} rows by a value of the {read.CacheType} record lookup '{lookup.Name}' finds; read that record's field with {LookupKey}: {lookup.Name}.<field> instead.");
                }

                key = (field, new FindAllOperand(null, null, lookup, path));
            }
            else
            {
                key = (field, new FindAllOperand(Column(operand, scope, $"{at}: {FindAllKey} '{line}'"), null, null, null));
            }
        }

        if (key is not { } found)
        {
            throw new FlowValidationException(
                $"{at}: {FindAllKey} needs the line the rows are found by, <field> = <value>, such as {Example}; '<field> is empty' only narrows the rows that line finds.");
        }

        if (empty.Contains(found.Field, StringComparer.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{at}: {FindAllKey} finds rows by {found.Field} and asks for {found.Field} to be empty; no row holds both.");
        }

        return new FindAllQuery(read.CacheType!, found.Field, found.Operand, empty);
    }

    /// <summary>A field of a cached row as a <c>$findAll</c> line names it: dotted names, never the language's own words.</summary>
    private static string CachedField(string field, string line, string at)
    {
        if (field.StartsWith(Marker, StringComparison.Ordinal)
            || field.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal)
            || field.Split('.').Any(p => !FieldPattern().IsMatch(p)))
        {
            throw new FlowValidationException(
                $"{at}: {FindAllKey} '{line}' names '{field}'; a line names a field of the rows the node reads, written as the cache names it, such as FieldIDList.");
        }

        return field;
    }

    /// <summary>
    /// A <c>$coalesce</c> node: its alternatives, in order, each a value node of its own (a column, an expression, the
    /// cache, a search or a literal, with its own findBy and modifiers); the node's value is the first of them that gives
    /// one. The node's <c>$when</c>, <c>$required</c> and description decide for all of them, so an alternative takes none
    /// of them. The entry returned is the first alternative, carrying the node's settings and the rest of the alternatives.
    /// </summary>
    private static MappingEntry Coalesce(IDictionary<object, object> map, IReadOnlyList<string> path, string location, TreeScope scope, int index, string source)
    {
        var at = $"{source}: {location}";
        const string Example = $"{CoalesceKey}: [ {{ {FromKey}: log_name }}, {{ {FromKey}: log_source }} ]";
        foreach (var key in map.Keys.Select(KeyText))
        {
            if (key == CoalesceKey || CoalesceSettings.Contains(key))
            {
                continue;
            }

            throw new FlowValidationException(ValueSettings.Contains(key)
                ? $"{at}: {key} belongs to one of the alternatives {CoalesceKey} lists, each of which reads its own value; move it into the alternative it is for."
                : $"{at}: a {CoalesceKey} node takes {CoalesceKey}, then {SettingList(CoalesceSettings)}, not '{key}'.");
        }

        var listed = Get(map, CoalesceKey) switch
        {
            IDictionary<object, object> => null,
            IEnumerable<object> items => items.ToList(),
            _ => null,
        } ?? throw new FlowValidationException(
            $"{at}: {CoalesceKey} lists the alternatives the value is taken from, in order, each a node of its own, such as {Example}.");
        if (listed.Count < 2)
        {
            throw new FlowValidationException(
                $"{at}: {CoalesceKey} lists {listed.Count} alternative{(listed.Count == 1 ? string.Empty : "s")}, and it takes the first of two or more that gives a value; one alternative is a node of its own, written without {CoalesceKey}.");
        }

        var alternatives = new List<MappingEntry>(listed.Count);
        for (var i = 0; i < listed.Count; i++)
        {
            var alternativeLocation = $"{location}.{CoalesceKey}[{i}]";
            var alternativeAt = $"{source}: {alternativeLocation}";
            if (listed[i] is not IDictionary<object, object> { Count: > 0 } alternative
                || !alternative.Keys.Select(KeyText).Any(key => SourceKeys.Contains(key) || key == ForEachKey))
            {
                throw new FlowValidationException(
                    $"{alternativeAt} is not a node: each alternative of {CoalesceKey} reads one value with {string.Join(", ", SourceKeys.Where(k => k != CoalesceKey))}, such as {{ {FromKey}: log_name }}.");
            }

            _ = IsNode(alternative, alternativeAt);
            if (Has(alternative, CoalesceKey))
            {
                throw new FlowValidationException($"{alternativeAt} lists alternatives of its own; write every alternative in the one {CoalesceKey} list, in the order they are tried.");
            }

            if (Has(alternative, ForEachKey) || Has(alternative, ItemKey))
            {
                throw new FlowValidationException($"{alternativeAt} repeats rows with {ForEachKey}, and an alternative of {CoalesceKey} reads one value.");
            }

            if (Has(alternative, ChildKey))
            {
                throw new FlowValidationException(
                    $"{alternativeAt}: a {ChildKey} node links one of the record's children, and gives nothing while OSDU does not hold it, which no other value stands in for; write it as a node of its own, or, to link several children, as the items of a list.");
            }

            foreach (var setting in CoalesceSettings.Where(setting => Has(alternative, setting)))
            {
                throw new FlowValidationException(
                    $"{alternativeAt}: {setting} decides for the whole {CoalesceKey} node; write it beside {CoalesceKey}, not in one of its alternatives.");
            }

            if (alternatives.Count > 0 && alternatives[^1].Static is not null)
            {
                throw new FlowValidationException(
                    $"{alternativeAt} is never tried: the literal before it always gives a value. A literal is the last alternative, the value taken when none of the others gives one.");
            }

            alternatives.Add(Value(alternative, path, alternativeLocation, scope, index, source) with { Required = false });
        }

        var required = Flag(map, RequiredKey, at) ?? true;
        return alternatives[0] with
        {
            Location = location,
            Alternatives = alternatives.Skip(1).ToList(),
            AppliesWhen = Has(map, WhenKey) ? Condition(Get(map, WhenKey), WhenKey, scope, at) : null,
            Required = required,
            Assertions = Assertions(map, "this node's value is the first its alternatives give", scope, required, location, source),
            Description = Text(map, DescriptionKey, at),
        };
    }

    /// <summary><c>$cache: UnitOfMeasure.id</c>: the cached type, then the field read, or a path into one.</summary>
    private static MappingSource CacheSource(string text, string at)
    {
        var parts = text.Split('.');
        if (parts.Length < 2 || !NamePattern().IsMatch(parts[0]) || parts.Skip(1).Any(p => !FieldPattern().IsMatch(p)))
        {
            throw new FlowValidationException(
                $"{at}: {CacheKey} names the cached type and the field it reads, such as {CacheKey}: UnitOfMeasure.id or {CacheKey}: CurveDictionary.log_curve_type_id; '{text}' is not one.");
        }

        return new MappingSource { Kind = MappingSourceKind.Cache, CacheType = parts[0], CacheField = string.Join('.', parts.Skip(1)) };
    }

    /// <summary>
    /// <c>$parent: data.DDMSDatasets</c>: a section of the record's parent, by its path from the record's root as the record
    /// writes it. The parent is the record's, found by the record's own id, so a node under a <c>$forEach</c>, which reads a
    /// row of a child dataset, cannot read it item by item: it takes the whole array instead.
    /// </summary>
    private static MappingSource ParentSource(string text, TreeScope scope, string at)
    {
        const string Example = ParentKey + ": data.DDMSDatasets";
        var path = text.Trim();
        if (scope.Child is not null)
        {
            throw new FlowValidationException(
                $"{at}: {ParentKey} reads the record's parent, which a row of {scope.Child} has none of; read the whole array from the parent instead, with {ParentKey} on the array itself (as {Example}).");
        }

        if (path.Contains("[]", StringComparison.Ordinal) || path.Contains('[', StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{at}: {ParentKey} '{path}' steps into an array, and a section of the parent is read whole: name the array itself, as {Example}, and it is taken with every item.");
        }

        if (!ParentPathPattern().IsMatch(path))
        {
            throw new FlowValidationException(
                $"{at}: {ParentKey} names the path of a section of the record's parent from the record's root, its property names joined by dots, as {Example} or {ParentKey}: data.ExtensionProperties.wdms; '{text}' is not one.");
        }

        return new MappingSource { Kind = MappingSourceKind.Parent, ParentPath = path };
    }

    /// <summary>
    /// <c>$base: data.Name</c>: a section of the record a fragment adds to, as the flow's own mapping and the steps of
    /// <c>enrich:</c> before this one left it, by its path from the record's root (osdu/docs/reference/flow/enrich.md). The
    /// record is one for the whole fragment, so a node under a <c>$forEach</c>, which reads a row of the step's dataset, reads
    /// no section of it of its own: it takes the whole array instead. Only a fragment reads one, which the mapping checks
    /// once the whole tree is read.
    /// </summary>
    private static MappingSource BaseSource(string text, TreeScope scope, string at)
    {
        const string Example = BaseKey + ": data.Name";
        var path = text.Trim();
        if (scope.Child is not null)
        {
            throw new FlowValidationException(
                $"{at}: {BaseKey} reads the record the fragment adds to, which is one for every row of {scope.Child}; read it outside the {ForEachKey}, or take a whole array of it with {BaseKey} on the array itself.");
        }

        if (path.Contains('[', StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{at}: {BaseKey} '{path}' steps into an array, and a section of the record is read whole: name the array itself, as {BaseKey}: data.Datasets, and it is taken with every item.");
        }

        if (!ParentPathPattern().IsMatch(path))
        {
            throw new FlowValidationException(
                $"{at}: {BaseKey} names the path of a section of the record from its root, its property names joined by dots, as {Example} or {BaseKey}: data.ExtensionProperties.WellDb; '{text}' is not one.");
        }

        return new MappingSource { Kind = MappingSourceKind.Base, BasePath = path };
    }

    /// <summary>
    /// <c>$child: welldb:wks:work-product-component--WellLogDetail:1.0.0</c>: the reference to the record's child of that kind, a
    /// kind whose template is linked to the mapping's own (osdu/docs/reference/flow/mapping-child.md). The child is found by the
    /// record's own id, so a node under a <c>$forEach</c>, which renders a row of a child dataset, has no child of its own.
    /// </summary>
    private static MappingSource ChildSource(string text, TreeScope scope, string at)
    {
        const string Example = ChildKey + ": welldb:wks:work-product-component--WellLogDetail:1.0.0";
        var kind = text.Trim();
        if (scope.Child is not null)
        {
            throw new FlowValidationException(
                $"{at}: {ChildKey} links one of the record's children, which are found by the record's own id, and a row of {scope.Child} has none of its own; write the link outside the {ForEachKey}.");
        }

        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new FlowValidationException(
                $"{at}: {ChildKey} names the kind of the child it links, 'authority:source:group--Entity:major.minor.patch', as {Example}; '{text}' is not one.");
        }

        if (!Identity.TargetId.EntityTypeFromKind(kind).Contains("--", StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{at}: {ChildKey} '{kind}' names an abstract schema, which no record is of; name the kind of the custom template linked to this one, as {Example}.");
        }

        return new MappingSource { Kind = MappingSourceKind.Child, ChildKind = kind };
    }

    /// <summary><c>$search: Wellbore</c>: one of the mapping's searches, which gives the id of the one record it finds.</summary>
    private static MappingSource SearchSource(string text, string at)
    {
        if (!NamePattern().IsMatch(text))
        {
            // A search asks the platform for the id of the record that matches and nothing else, so an answer is one id
            // whatever the mapping reads. What else a record needs comes from the dataset, the cache or a literal.
            throw new FlowValidationException(
                $"{at}: {SearchKey} names one of the mapping's searches, such as {SearchKey}: Wellbore, and gives the id of the record it finds; '{text}' is not a search name.");
        }

        return new MappingSource { Kind = MappingSourceKind.Search, CacheType = text, CacheField = "id" };
    }

    private static string FindByExample(string prefix)
        => prefix == MappingSource.SearchPrefix ? "data.FacilityName = wellbore_uwi" : "Code = unit";

    /// <summary>
    /// A cache or a search node's <c>$findBy</c>: one line or a list, each comparing a field of the record the node reads
    /// with a column or a quoted text, tried in order until one finds a record.
    /// </summary>
    private static List<FindBy> FindByLines(object? value, MappingSource read, TreeScope scope, string at)
    {
        var example = FindByExample(read.Prefix);
        List<string> lines = value switch
        {
            null => [],
            string one => [one],
            IEnumerable<object> many => many.Select(line => line as string
                ?? throw new FlowValidationException($"{at}: each {FindByKey} line is text, such as {example}.")).ToList(),
            _ => throw new FlowValidationException($"{at}: {FindByKey} is one line or a list of lines, such as {FindByKey}: {example}."),
        };

        var result = new List<FindBy>(lines.Count);
        foreach (var line in lines)
        {
            var match = FindByPattern().Match(line);
            if (!match.Success)
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' must read <field> = <column>, or <field> = 'text' for a fixed text, such as {example}.");
            }

            var field = match.Groups["field"].Value;
            if (field.StartsWith(Marker, StringComparison.Ordinal)
                || field.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal)
                || field.StartsWith(MappingSource.SearchPrefix + ".", StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' names '{field}'; a line compares a field of the record the node reads, written as the record names it, such as {example}.");
            }

            if (field.Split('.').Any(p => !FieldPattern().IsMatch(p)))
            {
                throw new FlowValidationException($"{at}: {FindByKey} '{line}' names an invalid field '{field}'.");
            }

            // A search compares a property of the records' data, named the way a query names it: unquoted, so only
            // letters, digits and underscores, and never the record's own metadata, which the indexer maps apart from
            // the schema and a lookup has no business comparing.
            if (read.Kind == MappingSourceKind.Search && (!field.StartsWith(SearchDataPrefix, StringComparison.Ordinal) || !OsduPath.IsPath(field)))
            {
                throw new FlowValidationException(
                    $"{at}: {FindByKey} '{line}' compares '{field}', and a search compares a property under data, named by dotted names of letters, digits and underscores, such as {example}.");
            }

            var operand = match.Groups["operand"].Value.Trim();
            if (Quoted(operand) is { } literal)
            {
                if (literal.Length == 0)
                {
                    throw new FlowValidationException($"{at}: {FindByKey} '{line}' compares with empty text.");
                }

                result.Add(new FindBy(read.CacheType!, field, null, literal) { Prefix = read.Prefix });
                continue;
            }

            result.Add(new FindBy(read.CacheType!, field, Column(operand, scope, $"{at}: {FindByKey} '{line}'"), null) { Prefix = read.Prefix });
        }

        return result;
    }

    /// <summary>
    /// A <c>$when</c> or a <c>$where</c>: an expression that gives true or false, such as <c>log_status = "FINAL"</c> or
    /// <c>not empty(curve_unit)</c>, read in the node's scope.
    /// </summary>
    private static MappingExpression Condition(object? value, string key, TreeScope scope, string at)
        => ReadCondition(ExpressionText(value, key, ConditionExample, at), key, scope.Child, out var problem)
            ?? throw new FlowValidationException($"{at}: {problem}");

    /// <summary><c>$expr: coalesce(log_name, log_source)</c>: a value computed from the row, read in the node's scope.</summary>
    private static MappingExpression Expression(object? value, string key, TreeScope scope, string at)
        => ReadExpression(ExpressionText(value, key, ExpressionExample, at), key, scope.Child, out var problem)
            ?? throw new FlowValidationException($"{at}: {problem}");

    private const string ConditionExample = "log_status = \"FINAL\"";

    private const string ExpressionExample = "coalesce(log_name, log_source)";

    /// <summary>
    /// A condition (<c>$when</c> or <c>$where</c>), or null with why it is refused: it does not read as an expression,
    /// it gives a value rather than true or false, or it reads no column and so decides the same for every row. The
    /// loader and the builder both ask this, so a draft is refused for what the loader would refuse it for.
    /// </summary>
    /// <param name="text">The condition as the tree writes it.</param>
    /// <param name="key">The key it is written under, for the message.</param>
    /// <param name="child">The child dataset whose rows a bare column name reads, or null for the dataset's own row.</param>
    /// <param name="problem">Why it is refused, or null.</param>
    internal static MappingExpression? ReadCondition(string text, string key, string? child, out string? problem)
    {
        if (OldCondition(text) is { } rewritten)
        {
            problem = $"{key} '{text}' is a condition as the mapping language no longer writes it; a condition is an expression now: {key}: {rewritten}";
            return null;
        }

        if (MappingExpression.TryParse(text, child, out var syntax) is not { } expression)
        {
            problem = $"{key} '{text}': {syntax}.";
            return null;
        }

        problem = !expression.IsCondition
            ? $"{key} '{text}' gives a value, and {key} is a condition, which gives true or false: compare the value, such as {key}: {ConditionExample}, or test it, such as {key}: not empty(log_status)."
            : expression.Columns.Count == 0
                ? $"{key} '{text}' reads no column, so it decides the same way for every row; remove it, or compare a column, such as {key}: {ConditionExample}."
                : null;
        return problem is null ? expression : null;
    }

    /// <summary>An expression a <c>$expr</c> node computes its value with, or null with why it is refused (see <see cref="ReadCondition"/>).</summary>
    internal static MappingExpression? ReadExpression(string text, string key, string? child, out string? problem)
    {
        if (MappingExpression.TryParse(text, child, out var syntax) is not { } expression)
        {
            problem = $"{key} '{text}': {syntax}.";
            return null;
        }

        problem = expression.Columns.Count == 0
            ? $"{key} '{text}' reads no column, so every record gets the same value; write it as a literal, whose text reads a parameter as {{{Marker}param.<name>}}."
            : null;
        return problem is null ? expression : null;
    }

    private static string ExpressionText(object? value, string key, string example, string at) => value switch
    {
        string written when !string.IsNullOrWhiteSpace(written) => written.Trim(),
        null or string => throw new FlowValidationException($"{at}: {key} is empty; write the expression, such as {key}: {example}."),
        IDictionary<object, object> or IEnumerable<object> => throw new FlowValidationException(
            $"{at}: {key} is one expression, written as text, such as {key}: {example}."),
        _ => ScalarText(value),
    };

    /// <summary>
    /// The expression a condition in the form the mapping language used to write (<c>log_status is FINAL</c>, <c>flag is
    /// not empty</c>) is written as now, or null when the text is not in that form.
    /// </summary>
    internal static string? OldCondition(string text)
    {
        var match = ConditionPattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var column = match.Groups["column"].Value;
        var negated = match.Groups["not"].Success;
        var rest = match.Groups["rest"].Value.Trim();
        if (rest == "empty")
        {
            return negated ? $"not empty({column})" : $"empty({column})";
        }

        var compared = (Quoted(rest) ?? rest).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"{column} {(negated ? "!=" : "=")} \"{compared}\"";
    }

    /// <summary>A node's modifiers, with the columns an id template names read in the node's scope.</summary>
    private static List<Modifier> Modifiers(object? value, bool written, TreeScope scope, string at)
    {
        if (!written)
        {
            return [];
        }

        if (value is not IEnumerable<object> list)
        {
            throw new FlowValidationException($"{at}: {ModifiersKey} is a list, such as {ModifiersKey}: [trim].");
        }

        return list.Select((modifier, i) => ParseModifier(modifier, scope.Child, $"{at} {ModifiersKey}[{i.ToString(CultureInfo.InvariantCulture)}]")).ToList();
    }

    /// <summary>
    /// A column a node names: <c>column</c> reads the row the node is in (under a <c>$forEach</c>, the item's row), and
    /// <c>$dataset.column</c> reads the dataset's own row from anywhere.
    /// </summary>
    private static DatasetColumn Column(string text, TreeScope scope, string where)
    {
        var trimmed = text.Trim();
        if (NamePattern().IsMatch(trimmed))
        {
            return new DatasetColumn(scope.Child, trimmed);
        }

        var parts = trimmed.Split('.');
        if (parts.Length == 2 && parts[0] == DatasetReference && NamePattern().IsMatch(parts[1]))
        {
            return new DatasetColumn(null, parts[1]);
        }

        if (parts.Length == 2 && NamePattern().IsMatch(parts[1]) && (parts[0] == DatasetColumn.Prefix || parts[0] == scope.Child))
        {
            throw new FlowValidationException(parts[0] == DatasetColumn.Prefix
                ? $"{where}: '{text}' reads a column the way the mapping language no longer writes it: a column of the dataset's own row is {DatasetReference}.{parts[1]}, and a column of {scope.Rows} is {parts[1]} alone."
                : $"{where}: '{text}' names {scope.Child} again; under the {ForEachKey} over {scope.Child} a bare name reads its row, so write {parts[1]}.");
        }

        throw new FlowValidationException(
            $"{where}: '{text}' is not a column; name a column of {scope.Rows} as it is, such as log_id, or a column of the dataset's own row as {DatasetReference}.<column>.");
    }

    /// <summary>A column as the node that reads it names it.</summary>
    private static string Written(DatasetColumn column, TreeScope scope)
        => column.Child is null && scope.Child is not null ? $"{DatasetReference}.{column.Column}" : column.Column;

    /// <summary>A key naming a property of the record: one name, never a path, and <c>$$name</c> for a name that starts with <c>$</c>.</summary>
    private static string PropertyName(string key, string at)
    {
        var name = PropertyOfKey(key);
        if (!PropertyPattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{at}: '{key}' is not a property name. A key in the record is one property of it, so nest the properties a path names rather than writing the path.");
        }

        return name;
    }

    private static TemplatePath Target(IReadOnlyList<string> path, string at)
        => TemplatePath.TryParse(TemplatePath.Prefix + "." + string.Join('.', path), out var target, out var error)
            ? target!
            : throw new FlowValidationException($"{at}: {error}.");

    /// <summary>Where, below a list, a node of the mapping language sits (<c>[0].Reviewers</c>), or null when it is literal throughout.</summary>
    private static string? MappedItem(object? node, string at)
    {
        switch (node)
        {
            case IDictionary<object, object> map when map.Keys.Select(KeyText).Any(IsNodeKey):
                return at;
            case IDictionary<object, object> map:
                foreach (var (key, value) in map)
                {
                    if (MappedItem(value, $"{at}.{KeyText(key)}") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case IEnumerable<object> list:
                foreach (var (item, i) in list.Select((item, i) => (item, i)))
                {
                    if (MappedItem(item, $"{at}[{i.ToString(CultureInfo.InvariantCulture)}]") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A literal written in the tree as the value it is: a scalar, or a list whose objects are laid out like the tree's own,
    /// a key <c>$$name</c> naming the property <c>$name</c>. What <c>$value</c> holds is read verbatim instead.
    /// </summary>
    private static JsonNode TreeLiteral(object node, string at)
    {
        var literal = node switch
        {
            IDictionary<object, object> map => new JsonObject(map.Select(kv => new KeyValuePair<string, JsonNode?>(
                PropertyOfKey(KeyText(kv.Key)),
                kv.Value is null ? throw new FlowValidationException($"{at}: '{KeyText(kv.Key)}' in the list has no value.") : TreeLiteral(kv.Value, at)))),
            IEnumerable<object> list => new JsonArray(list.Select(item => item is null
                ? throw new FlowValidationException($"{at}: a list holds an empty item.")
                : TreeLiteral(item, at)).ToArray()),
            _ => StaticValue(node, at) ?? throw new FlowValidationException($"{at} has no value; give it one, or remove it."),
        };

        CheckLiteralTexts(literal, at);
        return literal;
    }

    private static void CheckLiteralTexts(JsonNode literal, string at)
    {
        if (LiteralTokenProblem(literal) is { } problem)
        {
            throw new FlowValidationException($"{at}: {problem}");
        }
    }

    /// <summary>
    /// Why a literal is refused, or null. Its texts read no token of the mapping language but a parameter,
    /// <c>{$param.name}</c>: any other <c>{$...}</c> would be written into the record as it stands, and so would
    /// <c>{param.name}</c> without its marker, neither of which a record means to carry. The builder asks the same of a
    /// draft's literal before it is written.
    /// </summary>
    internal static string? LiteralTokenProblem(JsonNode literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        foreach (var text in StaticTexts(literal))
        {
            foreach (Match token in LiteralToken().Matches(text))
            {
                if (ParameterToken().IsMatch(token.Value))
                {
                    continue;
                }

                var inner = token.Groups["inner"].Value;
                return token.Groups["marker"].Success
                    ? $"the literal '{text}' holds {token.Value}, and a literal reads only a parameter, {{{Marker}param.<name>}}; read a column's value with {FromKey}, and build an id from values with the id modifier."
                    : $"the literal '{text}' holds {token.Value}; a parameter is read as {{{Marker}{inner}}}, with the mapping's '{Marker}' marker.";
            }
        }

        return null;
    }

    /// <summary>The nearest word to a misspelled one, when it is an obvious near miss: two edits away at most, or the same ignoring case.</summary>
    private static string? Nearest(string written, IEnumerable<string> known)
        => known
            .Select(candidate => (Word: candidate, Distance: string.Equals(candidate, written, StringComparison.OrdinalIgnoreCase) ? 0 : Distance(written, candidate)))
            .Where(candidate => candidate.Distance <= 2)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Word, StringComparer.Ordinal)
            .Select(candidate => candidate.Word)
            .FirstOrDefault();

    /// <summary>The edit distance between two words, capped: only near misses are worth suggesting.</summary>
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static bool Has(IDictionary<object, object> map, string key) => map.Keys.Any(k => KeyText(k) == key);

    private static object? Get(IDictionary<object, object> map, string key)
    {
        foreach (var (k, value) in map)
        {
            if (KeyText(k) == key)
            {
                return value;
            }
        }

        return null;
    }

    private static bool? Flag(IDictionary<object, object> map, string key, string at) => Get(map, key) switch
    {
        null when !Has(map, key) => null,
        bool flag => flag,
        _ => throw new FlowValidationException($"{at}: {key} is true or false."),
    };

    private static string? Text(IDictionary<object, object> map, string key, string at) => Get(map, key) switch
    {
        null => null,
        IDictionary<object, object> or IEnumerable<object> => throw new FlowValidationException($"{at}: {key} is one text."),
        var value => ScalarText(value),
    };

    private static string RequiredText(IDictionary<object, object> map, string key, string at)
        => Text(map, key, at)?.Trim() is { Length: > 0 } text ? text : throw new FlowValidationException($"{at}: {key} is empty.");

    /// <summary>A path into a record from its root: property names joined by dots, as <c>data.ExtensionProperties.wdms</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    internal static partial Regex ParentPathPattern();

    [GeneratedRegex(@"^[^\.\[\]\s]+$")]
    private static partial Regex PropertyPattern();

    /// <summary>A <c>$findAll</c> line a row meets by holding nothing under a field: <c>FieldList is empty</c>.</summary>
    [GeneratedRegex(@"^\s*(?<field>[^\s=]+)\s+is\s+empty\s*$")]
    private static partial Regex EmptyLinePattern();

    /// <summary>A token in braces, as a label, an id or a literal writes one.</summary>
    [GeneratedRegex(@"\{(?<token>[^{}]*)\}")]
    private static partial Regex BraceToken();

    /// <summary>What reads as a token of the mapping language inside a literal: <c>{$...}</c>, or a parameter written without its marker.</summary>
    [GeneratedRegex(@"\{\s*(?:(?<marker>\$)(?<inner>[^{}]*)|(?<inner>param\.[A-Za-z0-9_]+)\s*)\}")]
    private static partial Regex LiteralToken();

    [GeneratedRegex(MappingEntry.ParameterTokenPattern)]
    private static partial Regex ParameterToken();
}
