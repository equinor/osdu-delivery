using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>Who writes a template variable into a record.</summary>
public enum TemplateVariableRole
{
    /// <summary>A mapping fills it, or it is left out.</summary>
    Mapping,

    /// <summary>The engine writes it: the record id from the dataset key, the kind from the template.</summary>
    Engine,

    /// <summary>OSDU sets it when the record is stored.</summary>
    Osdu,
}

/// <summary>The shape of the value a template variable takes.</summary>
public enum TemplateVariableShape
{
    /// <summary>One value: text, a number, a boolean.</summary>
    Value,

    /// <summary>A list of values.</summary>
    ValueList,

    /// <summary>An object whose properties are variables of their own.</summary>
    Group,

    /// <summary>A list of objects whose properties are variables of their own: a repeater fills it from a child dataset.</summary>
    GroupList,

    /// <summary>
    /// An object or list the schema does not break into named properties (a choice of shapes, free-form content): a static
    /// value fills it whole, and an open object (<see cref="TemplateVariable.Open"/>) takes entries inside it as well.
    /// </summary>
    Whole,
}

/// <summary>One variable of a template: a property of the OSDU record, with what the schema says about it.</summary>
public sealed record TemplateVariable
{
    public required TemplatePath Path { get; init; }

    public required TemplateVariableShape Shape { get; init; }

    /// <summary>The JSON Schema type: string, number, integer, boolean, object, array, or any.</summary>
    public required string Type { get; init; }

    /// <summary>The type of each item of a list of values.</summary>
    public string? ItemType { get; init; }

    public string? Format { get; init; }

    /// <summary>Whether the schema requires the property in the object that holds it.</summary>
    public bool Required { get; init; }

    /// <summary>The entity types the property points to (<c>master-data--Wellbore</c>), or a group type alone (<c>dataset</c>).</summary>
    public IReadOnlyList<string> Relationships { get; init; } = [];

    /// <summary>The pattern a text value must match, when the schema declares one.</summary>
    public string? Pattern { get; init; }

    /// <summary>The schema's unit or frame-of-reference context (<c>UOM:length</c>).</summary>
    public string? UnitContext { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public TemplateVariableRole Role { get; init; } = TemplateVariableRole.Mapping;

    /// <summary>For an object with free keys (<c>tags</c>): the type of the value under any key. Mappings target <c>osdu.tags.&lt;name&gt;</c>.</summary>
    public string? KeyValueType { get; init; }

    /// <summary>A list of objects inside a repeated item: listed for reference, but not fillable, because a repeater inside a repeater is not supported.</summary>
    public bool Nested { get; init; }

    /// <summary>
    /// An object the schema leaves open (<c>data.ExtensionProperties</c>): it declares no properties, names no type for
    /// its keys and refuses none. A mapping lays out what it writes inside it, at any depth, as it lays out the record.
    /// </summary>
    public bool Open { get; init; }

    /// <summary>
    /// For a path inside an open object: that object. Nothing in the schema types or shapes what is written there, so the
    /// variable takes a value, an object, a list or a repeater's items alike, written as they arrive.
    /// </summary>
    public TemplatePath? Inside { get; init; }
}

/// <summary>
/// A template: the OSDU record of one kind with a variable for every property the schema declares
/// (osdu/docs/reference/concepts/templates.md). It is generated from the schema and never edited; mappings name its
/// variables.
/// </summary>
public sealed class OsduTemplate
{
    /// <summary>How deep the walk follows nested objects; OSDU schemas stay well inside it, and it stops a self-referencing schema.</summary>
    public const int MaxDepth = 12;

    private static readonly HashSet<string> EngineProperties = new(StringComparer.Ordinal) { "id", "kind" };

    private static readonly HashSet<string> OsduProperties = new(StringComparer.Ordinal) { "version", "createTime", "createUser", "modifyTime", "modifyUser" };

    private readonly Dictionary<string, TemplateVariable> _byPath;

    private OsduTemplate(SchemaSnapshot schema, IReadOnlyList<TemplateVariable> variables)
    {
        Schema = schema;
        Variables = variables;
        _byPath = variables.ToDictionary(v => v.Path.Text, StringComparer.Ordinal);
    }

    public SchemaSnapshot Schema { get; }

    public string Kind => Schema.Kind;

    public string Version => Schema.Version;

    public string? Title => Schema.Root["title"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public string? Description => Schema.Root["description"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Every variable, in the order the schema declares the properties, parents before their children.</summary>
    public IReadOnlyList<TemplateVariable> Variables { get; }

    /// <summary>The variables a mapping can fill.</summary>
    public IEnumerable<TemplateVariable> Fillable => Variables.Where(v => v.Role == TemplateVariableRole.Mapping && !v.Nested);

    /// <summary>
    /// The variable at <paramref name="path"/>, or null when the template has none. A key under an object with free keys
    /// (<c>osdu.tags.WellLogNativeUID</c>) is a variable of that object's value type, and a path inside an open object
    /// (<c>osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit</c>) a variable of no type, <see cref="TemplateVariable.Inside"/> it.
    /// </summary>
    public TemplateVariable? Find(TemplatePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_byPath.TryGetValue(path.Text, out var variable))
        {
            return variable;
        }

        if (path.Parent is { } parent && !path.Segments[^1].IntoArray && _byPath.TryGetValue(parent.Text, out var holder)
            && holder.KeyValueType is { } valueType && path.Segments.Count(s => s.IntoArray) == holder.Path.Segments.Count(s => s.IntoArray))
        {
            return new TemplateVariable
            {
                Path = path,
                Shape = valueType is "object" or "array" or "any" ? TemplateVariableShape.Whole : TemplateVariableShape.Value,
                Type = valueType,
                Role = holder.Role,
                Description = holder.Description,
            };
        }

        return OpenObjectOf(path) is { } open
            ? new TemplateVariable
            {
                Path = path,
                Shape = TemplateVariableShape.Whole,
                Type = "any",
                Role = open.Role,
                Description = open.Description,
                Inside = open.Path,
            }
            : null;
    }

    /// <summary>
    /// The open object <paramref name="path"/> lies inside, or null. The path reaches it with the same steps into arrays the
    /// object's own path takes, and never steps into the object itself, which is no list.
    /// </summary>
    public TemplateVariable? OpenObjectOf(TemplatePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        for (var holder = path.Parent; holder is not null; holder = holder.Parent)
        {
            if (_byPath.TryGetValue(holder.Text, out var candidate))
            {
                return candidate.Open && !path.Segments[holder.Segments.Count - 1].IntoArray ? candidate : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Why the template has no variable at <paramref name="path"/>, a path <see cref="Find"/> answers null for, when the
    /// deepest variable the path goes through tells: a path that runs past a free key of an object that takes any name
    /// (<c>osdu.tags.lra.securityClassification</c>, where <c>tags</c> takes a text under <c>lra</c>), or a name holding a
    /// dot, a bracket or a space under an object that declares its properties by name. Null when nothing more is known than
    /// that the template has no such variable.
    /// </summary>
    public string? Missing(TemplatePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Find(path) is not null)
        {
            return null;
        }

        for (var holder = path.Parent; holder is not null; holder = holder.Parent)
        {
            if (!_byPath.TryGetValue(holder.Text, out var variable))
            {
                continue;
            }

            var below = path.Segments.Skip(holder.Segments.Count).ToList();
            if (variable.KeyValueType is { } valueType && below.Count > 1)
            {
                var name = string.Join('.', below.Select(s => s.Name));
                var under = TemplatePath.TextOf(RecordPathNotation.Append(holder.RecordPath, below[0].Name));
                return $"{holder.Text} takes {Described(valueType)} under each name it holds, so {under} holds no properties of its own. "
                    + $"A name holding a dot is one key: write '{name}' as one key under {holder.Leaf}, which fills {TemplatePath.TextOf(RecordPathNotation.Append(holder.RecordPath, name))}";
            }

            var first = below[0].Name;
            if (variable.Shape is TemplateVariableShape.Group or TemplateVariableShape.GroupList && !RecordPathNotation.IsPlain(first))
            {
                // A path written as one key (GeoContexts.FieldID, Curves[].CurveID) the template has when it is read as a path.
                var meant = TemplatePath.TryParse($"{holder.Text}{(variable.Shape == TemplateVariableShape.GroupList ? "[]" : string.Empty)}.{first}", out var written, out _)
                    && Find(written!) is not null
                        ? $"; to write {written!.Text}, nest the properties a path names rather than writing the path"
                        : string.Empty;
                return $"{holder.Text} declares its properties by name, and none is called '{first}'. A key is one property name, and a name "
                    + $"holding a dot, a bracket or a space is written only where the template takes names it does not declare, such as tags or an open object{meant}";
            }

            return null;
        }

        return null;
    }

    private static string Described(string valueType) => valueType switch
    {
        "string" => "a text",
        "integer" => "an integer",
        "object" or "array" => $"an {valueType}",
        "any" => "any value",
        _ => $"a {valueType}",
    };

    /// <summary>Builds the template of a schema.</summary>
    public static OsduTemplate From(SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var variables = new List<TemplateVariable>();
        Walk(schema, schema.EffectiveRootObject, [], depth: 0, insideArray: false, variables);
        return new OsduTemplate(schema, variables);
    }

    private static void Walk(
        SchemaSnapshot schema, JsonObject effective, List<TemplatePathSegment> prefix, int depth, bool insideArray, List<TemplateVariable> variables)
    {
        if (effective["properties"] is not JsonObject properties)
        {
            return;
        }

        var required = Names(effective["required"]);
        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject raw)
            {
                continue;
            }

            var own = schema.EffectiveOf(raw);
            var type = SchemaSnapshot.TypeOfNode(own);
            var segments = new List<TemplatePathSegment>(prefix) { new(name, false) };
            var role = depth == 0 && EngineProperties.Contains(name)
                ? TemplateVariableRole.Engine
                : depth == 0 && OsduProperties.Contains(name) ? TemplateVariableRole.Osdu : TemplateVariableRole.Mapping;

            var variable = new TemplateVariable
            {
                Path = TemplatePath.Of(segments),
                Shape = TemplateVariableShape.Value,
                Type = TypeName(type),
                Format = Text(raw, "format") ?? Text(own, "format"),
                Required = required.Contains(name),
                Relationships = Relationships(raw, own),
                Pattern = Text(raw, "pattern") ?? Text(own, "pattern"),
                UnitContext = Text(raw, "x-osdu-frame-of-reference") ?? Text(own, "x-osdu-frame-of-reference"),
                Title = Text(raw, "title") ?? Text(own, "title"),
                Description = Text(raw, "description") ?? Text(own, "description"),
                Role = role,
            };

            switch (type)
            {
                case SchemaType.Array:
                    {
                        var items = own["items"] is JsonObject itemNode ? schema.EffectiveOf(itemNode) : null;
                        var itemType = items is null ? SchemaType.Any : SchemaSnapshot.TypeOfNode(items);
                        var itemRelationships = items is null ? [] : Relationships(own["items"] as JsonObject ?? items, items);
                        if (itemType == SchemaType.Object && items!["properties"] is JsonObject { Count: > 0 })
                        {
                            variables.Add(variable with { Shape = TemplateVariableShape.GroupList, ItemType = "object", Nested = insideArray });
                            if (!insideArray && depth < MaxDepth)
                            {
                                var into = new List<TemplatePathSegment>(prefix) { new(name, true) };
                                Walk(schema, items, into, depth + 1, insideArray: true, variables);
                            }
                        }
                        else if (itemType is SchemaType.Object or SchemaType.Array or SchemaType.Any)
                        {
                            variables.Add(variable with { Shape = TemplateVariableShape.Whole, ItemType = TypeName(itemType), Relationships = itemRelationships.Count > 0 ? itemRelationships : variable.Relationships });
                        }
                        else
                        {
                            variables.Add(variable with
                            {
                                Shape = TemplateVariableShape.ValueList,
                                ItemType = TypeName(itemType),
                                Relationships = itemRelationships.Count > 0 ? itemRelationships : variable.Relationships,
                                Pattern = variable.Pattern ?? Text(items!, "pattern"),
                            });
                        }

                        break;
                    }

                case SchemaType.Object:
                    if (own["properties"] is JsonObject { Count: > 0 })
                    {
                        variables.Add(variable with { Shape = TemplateVariableShape.Group });
                        if (depth < MaxDepth)
                        {
                            Walk(schema, own, segments, depth + 1, insideArray, variables);
                        }
                    }
                    else if (own["additionalProperties"] is JsonObject additional)
                    {
                        var valueType = SchemaSnapshot.TypeOfNode(schema.EffectiveOf(additional));
                        variables.Add(variable with { Shape = TemplateVariableShape.Whole, KeyValueType = TypeName(valueType) });
                    }
                    else
                    {
                        variables.Add(variable with { Shape = TemplateVariableShape.Whole, Open = IsOpen(own) });
                    }

                    break;

                case SchemaType.Any:
                    variables.Add(variable with { Shape = own["oneOf"] is not null || own["anyOf"] is not null ? TemplateVariableShape.Whole : TemplateVariableShape.Value });
                    break;

                default:
                    variables.Add(variable);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether an object that declares no properties and no type for its keys takes any content: one that refuses
    /// undeclared keys (<c>additionalProperties: false</c>) can only be empty, and one offering a choice of forms is one of
    /// those forms, not free content.
    /// </summary>
    private static bool IsOpen(JsonObject own)
        => !(own["additionalProperties"] is JsonValue closed && closed.GetValueKind() == System.Text.Json.JsonValueKind.False)
            && own["oneOf"] is null
            && own["anyOf"] is null;

    private static HashSet<string> Names(JsonNode? node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    internal static IReadOnlyList<string> Relationships(JsonObject raw, JsonObject effective)
    {
        var declared = raw["x-osdu-relationship"] as JsonArray ?? effective["x-osdu-relationship"] as JsonArray;
        if (declared is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var entry in declared.OfType<JsonObject>())
        {
            var group = Text(entry, "GroupType");
            var entity = Text(entry, "EntityType");
            var name = group is not null && entity is not null ? $"{group}--{entity}" : group ?? entity;
            if (name is not null && !result.Contains(name, StringComparer.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string TypeName(SchemaType type) => type switch
    {
        SchemaType.String => "string",
        SchemaType.Number => "number",
        SchemaType.Integer => "integer",
        SchemaType.Boolean => "boolean",
        SchemaType.Object => "object",
        SchemaType.Array => "array",
        _ => "any",
    };
}
