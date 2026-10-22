using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// One OSDU kind's JSON Schema, bundled so every <c>$ref</c> is local (<c>#/definitions/...</c>), immutable and
/// content-addressed (osdu/docs/reference/concepts/templates.md, Template versions). The version is the hash of the
/// canonical schema, so an upstream schema edit under the same kind string still moves the render context.
/// </summary>
public sealed class SchemaSnapshot
{
    private readonly JsonObject _root;
    private readonly JsonObject? _definitions;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SchemaProperty?> _resolved = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SchemaProperty?> _resolvedThroughForms = new(StringComparer.Ordinal);

    /// <summary>How deep the forms of a <c>oneOf</c> or <c>anyOf</c> are looked into; OSDU schemas nest one or two.</summary>
    private const int MaxFormDepth = 4;

    private static readonly string[] Choices = ["oneOf", "anyOf"];
    private JsonObject? _effectiveRoot;
    private (Templates.TemplateParent? Parent, string? Problem)? _parent;

    public SchemaSnapshot(string kind, JsonObject bundledSchema, DateTimeOffset capturedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(bundledSchema);
        Kind = kind;
        _root = bundledSchema;
        _definitions = bundledSchema["definitions"] as JsonObject ?? bundledSchema["$defs"] as JsonObject;
        CapturedUtc = capturedUtc;
        Version = ContentHash.Of(CanonicalJson.ToBytes(bundledSchema))[..16];
    }

    public string Kind { get; }

    /// <summary>Content hash prefix of the bundled schema. Enters the render context.</summary>
    public string Version { get; }

    /// <summary>
    /// The record kind the template is linked to as its parent (<see cref="Templates.TemplateParent"/>), read from its
    /// <c>x-osdu-inheriting-from-kind</c>; null for a template linked to none, or whose link cannot be read
    /// (<see cref="ParentProblem"/> says why).
    /// </summary>
    public Templates.TemplateParent? Parent => ParentLink.Parent;

    /// <summary>Why the template's <c>x-osdu-inheriting-from-kind</c> names no parent that can be read; null when it names one or none.</summary>
    public string? ParentProblem => ParentLink.Problem;

    private (Templates.TemplateParent? Parent, string? Problem) ParentLink
        => _parent ??= (Templates.TemplateParent.Read(_root, out var problem), problem);

    public DateTimeOffset CapturedUtc { get; }

    public JsonObject Root => _root;

    /// <summary>Parses a bundled schema document as saved by the snapshot store.</summary>
    public static SchemaSnapshot Parse(string kind, string json, DateTimeOffset capturedUtc)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new DeliveryException($"Schema snapshot for '{kind}' is not a JSON object.");
        return new SchemaSnapshot(kind, node, capturedUtc);
    }

    /// <summary>
    /// Resolves a dotted path from the record root (for example <c>data.Curves.CurveID</c>, where an array segment
    /// is stepped into implicitly) to its effective schema, merging <c>allOf</c> branches and following local refs.
    /// Returns null when no such property exists.
    /// </summary>
    public SchemaProperty? Resolve(string dottedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);
        return _resolved.GetOrAdd(dottedPath, ResolveUncached);
    }

    private SchemaProperty? ResolveUncached(string dottedPath)
    {
        var current = EffectiveRoot;
        SchemaProperty? result = null;
        foreach (var segment in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Child(current, segment);
            if (next is null)
            {
                return null;
            }

            result = next;
            current = next.Schema;
            // A collection property binds its children to the array item schema.
            if (next.Type == SchemaType.Array && next.Items is not null)
            {
                current = next.Items;
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a dotted path as <see cref="Resolve"/> does, and where an object declares no such property, looks into the
    /// forms its <c>oneOf</c> or <c>anyOf</c> allows: a wellbore's <c>GeoContexts</c> holds one of five kinds of context, and
    /// only the field's declares <c>FieldID</c>. The property is the first form's that declares it; a type the node does not
    /// declare is the one every form shares. Returns null when neither the object nor any form declares it.
    /// </summary>
    public SchemaProperty? ResolveThroughForms(string dottedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);
        return _resolvedThroughForms.GetOrAdd(dottedPath, ResolveThroughFormsUncached);
    }

    private SchemaProperty? ResolveThroughFormsUncached(string dottedPath)
    {
        var current = EffectiveRoot;
        SchemaProperty? result = null;
        foreach (var segment in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Child(current, segment) ?? ChildOfForms(current, segment);
            if (next is null)
            {
                return null;
            }

            if (next.Type == SchemaType.Any && TypeWithForms(next.Schema) is var shared && shared != SchemaType.Any)
            {
                next = next with { Type = shared };
            }

            result = next;
            current = next.Schema;
            if (next.Type == SchemaType.Array && next.Items is not null)
            {
                current = next.Items;
            }
        }

        return result;
    }

    /// <summary>The property <paramref name="name"/> the first form of <paramref name="holder"/>'s <c>oneOf</c> or <c>anyOf</c> declaring it declares; null for none.</summary>
    private SchemaProperty? ChildOfForms(JsonObject holder, string name)
    {
        foreach (var (form, _) in FormsOf(holder))
        {
            if (form["properties"] is JsonObject props && props[name] is JsonObject child)
            {
                return Describe(name, Effective(child));
            }
        }

        return null;
    }

    /// <summary>
    /// Every form a <c>oneOf</c> or <c>anyOf</c> of <paramref name="holder"/> allows, forms of forms included, each effective
    /// and with its title (its own, else the definition its reference names). A form whose reference the bundle does not hold
    /// describes nothing and is passed over.
    /// </summary>
    public IEnumerable<(JsonObject Schema, string Title)> FormsOf(JsonObject holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return Forms(holder, 0);
    }

    private IEnumerable<(JsonObject Schema, string Title)> Forms(JsonObject holder, int depth)
    {
        if (depth >= MaxFormDepth)
        {
            yield break;
        }

        foreach (var keyword in Choices)
        {
            if (holder[keyword] is not JsonArray forms)
            {
                continue;
            }

            foreach (var form in forms.OfType<JsonObject>())
            {
                JsonObject effective;
                try
                {
                    effective = Effective(form);
                }
                catch (DeliveryException)
                {
                    continue;
                }

                var title = TitleOf(effective) ?? TitleOf(form) ?? RefName(form) ?? keyword;
                yield return (effective, title);
                foreach (var inner in Forms(effective, depth + 1))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// The type a node declares; for one that declares none but allows a choice of forms (a wellbore's geographic context),
    /// the type every form shares, else <see cref="SchemaType.Any"/>.
    /// </summary>
    public SchemaType TypeWithForms(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var type = TypeOf(node);
        if (type != SchemaType.Any)
        {
            return type;
        }

        var forms = FormsOf(node).Select(f => TypeOf(f.Schema)).Where(t => t != SchemaType.Any).Distinct().ToList();
        return forms.Count == 1 ? forms[0] : SchemaType.Any;
    }

    private static string? TitleOf(JsonObject node)
        => node["title"] is JsonValue value && value.TryGetValue<string>(out var title) && !string.IsNullOrWhiteSpace(title) ? title : null;

    /// <summary>The definition a local reference names (<c>AbstractGeoFieldContext.1.0.0</c> of <c>#/definitions/AbstractGeoFieldContext.1.0.0</c>).</summary>
    private static string? RefName(JsonObject form)
        => form["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && !string.IsNullOrWhiteSpace(reference)
            ? reference[(reference.LastIndexOf('/') + 1)..]
            : null;

    /// <summary>The required property names of the object at <paramref name="dottedPath"/> (empty path is the record root).</summary>
    public IReadOnlyList<string> RequiredAt(string dottedPath)
    {
        var effective = string.IsNullOrEmpty(dottedPath)
            ? EffectiveRoot
            : Resolve(dottedPath) is { } p ? (p.Type == SchemaType.Array && p.Items is not null ? p.Items : p.Schema) : null;
        if (effective is null)
        {
            return [];
        }

        return effective["required"] is JsonArray required
            ? required.Select(r => r?.GetValue<string>()).Where(r => r is not null).Cast<string>().ToList()
            : [];
    }

    /// <summary>The property names of the object at <paramref name="dottedPath"/>.</summary>
    public IReadOnlyList<string> PropertiesAt(string dottedPath)
    {
        var effective = string.IsNullOrEmpty(dottedPath)
            ? EffectiveRoot
            : Resolve(dottedPath) is { } p ? (p.Type == SchemaType.Array && p.Items is not null ? p.Items : p.Schema) : null;
        return effective?["properties"] is JsonObject props ? props.Select(kv => kv.Key).ToList() : [];
    }

    private JsonObject EffectiveRoot => _effectiveRoot ??= Effective(_root);

    /// <summary>The effective root object: every <c>allOf</c> branch merged and every <c>$ref</c> followed.</summary>
    internal JsonObject EffectiveRootObject => EffectiveRoot;

    /// <summary>A schema node with its <c>allOf</c> branches merged and its <c>$ref</c> followed, for walks over the whole schema.</summary>
    internal JsonObject EffectiveOf(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return Effective(schema);
    }

    /// <summary>The JSON Schema type an effective node declares, or implies through its properties or items.</summary>
    internal static SchemaType TypeOfNode(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return TypeOf(schema);
    }

    private SchemaProperty? Child(JsonObject effectiveObject, string name)
    {
        if (effectiveObject["properties"] is JsonObject props && props[name] is JsonObject child)
        {
            return Describe(name, Effective(child));
        }

        // additionalProperties: {schema} (for example OSDU tags: string values keyed by anything).
        if (effectiveObject["additionalProperties"] is JsonObject additional)
        {
            return Describe(name, Effective(additional));
        }

        if (effectiveObject["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var allowed) && allowed)
        {
            return new SchemaProperty(name, SchemaType.Any, new JsonObject(), null, null, false);
        }

        return null;
    }

    private SchemaProperty Describe(string name, JsonObject effective)
    {
        var type = TypeOf(effective);
        JsonObject? items = null;
        if (type == SchemaType.Array && effective["items"] is JsonObject itemSchema)
        {
            items = Effective(itemSchema);
        }

        var pattern = effective["pattern"]?.GetValue<string>();
        var relationship = effective["x-osdu-relationship"] is JsonArray;
        return new SchemaProperty(name, type, effective, items, pattern, relationship);
    }

    /// <summary>Merges <c>allOf</c> branches and resolves <c>$ref</c> into one object schema.</summary>
    private JsonObject Effective(JsonObject schema)
    {
        if (schema["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
        {
            var target = ResolveRef(reference);
            return Effective(target);
        }

        if (schema["allOf"] is not JsonArray allOf)
        {
            return schema;
        }

        var merged = new JsonObject();
        var mergedProps = new JsonObject();
        var mergedRequired = new JsonArray();
        foreach (var kv in schema)
        {
            if (kv.Key is "allOf")
            {
                continue;
            }

            merged[kv.Key] = kv.Value?.DeepClone();
        }

        var branches = allOf.OfType<JsonObject>().Select(Effective).Append(merged).ToList();
        foreach (var branch in branches)
        {
            if (branch["properties"] is JsonObject props)
            {
                foreach (var p in props)
                {
                    mergedProps[p.Key] = p.Value?.DeepClone();
                }
            }

            if (branch["required"] is JsonArray req)
            {
                foreach (var r in req)
                {
                    if (r is not null && !mergedRequired.Any(existing => existing?.GetValue<string>() == r.GetValue<string>()))
                    {
                        mergedRequired.Add(r.DeepClone());
                    }
                }
            }

            foreach (var kv in branch)
            {
                if (kv.Key is "properties" or "required" or "allOf" or "$ref")
                {
                    continue;
                }

                merged[kv.Key] ??= kv.Value?.DeepClone();
            }
        }

        merged["properties"] = mergedProps;
        merged["required"] = mergedRequired;
        merged["type"] ??= "object";
        return merged;
    }

    private JsonObject ResolveRef(string reference)
    {
        const string prefix = "#/definitions/";
        const string prefix2 = "#/$defs/";
        string name;
        if (reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            name = reference[prefix.Length..];
        }
        else if (reference.StartsWith(prefix2, StringComparison.Ordinal))
        {
            name = reference[prefix2.Length..];
        }
        else
        {
            throw new DeliveryException(
                $"Schema snapshot for '{Kind}' contains a non-local reference '{reference}'. Snapshots must be bundled so every $ref is '#/definitions/...'.");
        }

        return _definitions?[name] as JsonObject
            ?? throw new DeliveryException($"Schema snapshot for '{Kind}' has no definition '{name}' (referenced as '{reference}').");
    }

    private static SchemaType TypeOf(JsonObject schema)
    {
        var typeNode = schema["type"];
        string? type = typeNode switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray arr => arr.Select(a => a?.GetValue<string>()).FirstOrDefault(a => a is not null and not "null"),
            _ => null,
        };

        if (type is null)
        {
            if (schema["properties"] is not null || schema["allOf"] is not null)
            {
                return SchemaType.Object;
            }

            if (schema["items"] is not null)
            {
                return SchemaType.Array;
            }

            return SchemaType.Any;
        }

        return type switch
        {
            "string" => SchemaType.String,
            "number" => SchemaType.Number,
            "integer" => SchemaType.Integer,
            "boolean" => SchemaType.Boolean,
            "object" => SchemaType.Object,
            "array" => SchemaType.Array,
            _ => SchemaType.Any,
        };
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The names mirror JSON Schema type keywords.")]
public enum SchemaType
{
    Any,
    String,
    Number,
    Integer,
    Boolean,
    Object,
    Array,
}

/// <summary>A resolved property of a schema snapshot.</summary>
public sealed record SchemaProperty(
    string Name,
    SchemaType Type,
    JsonObject Schema,
    JsonObject? Items,
    string? Pattern,
    bool IsRelationship)
{
    public string? Format => Schema["format"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The format of each item of a list of values (a list of dates, say), when the schema declares one.</summary>
    public string? ItemFormat => Items?["format"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The element type of an array (<see cref="SchemaType.Object"/> for an array of objects), or null for a non-array.</summary>
    public SchemaType? ItemScalarType
    {
        get
        {
            if (Type != SchemaType.Array || Items is null)
            {
                return null;
            }

            var t = Items["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            return t switch
            {
                "string" => SchemaType.String,
                "number" => SchemaType.Number,
                "integer" => SchemaType.Integer,
                "boolean" => SchemaType.Boolean,
                "object" => SchemaType.Object,
                _ => null,
            };
        }
    }
}
