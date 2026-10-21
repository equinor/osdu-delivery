using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// Where a template's schema comes from: the OSDU data definitions, read from the public repository
/// (<see cref="OsduDataDefinitions"/>) or from a local checkout of it, or a bundled schema file for a schema of one's own.
/// Each produces the same thing, a <see cref="SchemaSnapshot"/> whose every <c>$ref</c> is resolved into its definitions,
/// which the template store saves.
/// </summary>
public static class TemplateSources
{
    /// <summary>How deep <see cref="DeclaresData(JsonObject)"/> follows nested <c>allOf</c> branches.</summary>
    private const int MaxAllOfDepth = 8;

    /// <summary>Bundles a kind's schema from a local checkout of the OSDU data definitions (the repository's <c>Generated</c> folder).</summary>
    public static async Task<SchemaSnapshot> FromDirectoryAsync(string dataRoot, string kind, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(time);
        RequireKind(kind);
        var root = Path.GetFullPath(dataRoot);
        if (!Directory.Exists(root))
        {
            throw new DeliveryException($"The data definitions folder '{root}' does not exist.");
        }

        var path = SchemaBundler.KindPath(kind);
        var bundled = await SchemaBundler.BundleTreeAsync(path, (file, token) => ReadCheckoutFileAsync(root, file, token), ct).ConfigureAwait(false);
        return Validated(kind, bundled, time.GetUtcNow(), Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Reads a bundled schema (every <c>$ref</c> under <c>#/definitions/</c> and present), as the data definitions bundle
    /// it and as the import accepts it. <paramref name="where"/> names the file or upload in errors.
    /// </summary>
    public static SchemaSnapshot FromBundledJson(string json, string kind, DateTimeOffset capturedUtc, string where)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        RequireKind(kind);
        return Validated(kind, ParseObject(json, where), capturedUtc, where);
    }

    /// <summary>
    /// A template as a file: the bundled schema indented, its keys in the order the schema was written, which is the form
    /// <c>osdu/samples/welldb/templates</c> keeps the sample schemas in. <see cref="FromBundledJson"/> reads it back as the same
    /// version, since a version is the hash of the canonical form, so a copy kept in a repository can be checked against
    /// the version its mappings pin by importing it. The control plane answers it as the file <see cref="FileName"/> names,
    /// which the Templates page downloads, and <c>sqlflow template</c> writes it with <c>--out</c>.
    /// </summary>
    public static string FileText(SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return DocumentJson.Indented(schema.Root);
    }

    /// <summary>
    /// The name a template's file goes by: its kind, each colon an underscore
    /// (<c>osdu_wks_work-product-component--WellLog_1.4.0.json</c>), as <c>osdu/samples/welldb/templates</c> names the sample
    /// schemas. A record kind holds only letters, digits, underscores, dots and dashes between its colons, so every file
    /// system takes the name as it is.
    /// </summary>
    public static string FileName(string kind)
    {
        RequireKind(kind);
        return kind.Replace(':', '_') + ".json";
    }

    /// <summary>
    /// Whether the schema declares the <c>data</c> property every OSDU record carries, itself or in an <c>allOf</c> branch
    /// it holds. This is the rule <see cref="Validated"/> enforces on a bundled schema, asked of a file that is not bundled
    /// yet, so a branch that is a <c>$ref</c> to another file is left alone: a published record schema declares <c>data</c>
    /// in its own file, and the files it refers to are what sits under <c>data</c>, not what declares it. The abstract
    /// building blocks, the manifest and the content schemas declare none, so no template can be laid out from them.
    /// </summary>
    public static bool DeclaresData(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return DeclaresData(schema, depth: 0);
    }

    /// <summary>
    /// Whether the schema refers to anything outside itself, as a schema file the OSDU data definitions publish refers to the
    /// shared schemas beside it (<c>../abstract/AbstractAccessControlList.1.0.0.json</c>).
    /// </summary>
    public static bool RefersOutside(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return References(schema).Any(reference => !reference.StartsWith('#'));
    }

    /// <summary>Schema text as the JSON object a schema is; <paramref name="where"/> names it in the error.</summary>
    internal static JsonObject ParseObject(string json, string where)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new DeliveryException($"{where}: the schema is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{where}: the schema is not valid JSON ({ex.Message}).", ex);
        }
    }

    /// <summary>Whether a schema names the kind it describes in its own <c>x-osdu-schema-source</c>.</summary>
    public static bool DeclaresKind(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema["x-osdu-schema-source"] is JsonValue declared && declared.TryGetValue<string>(out var kind) && !string.IsNullOrWhiteSpace(kind);
    }

    /// <summary>
    /// <paramref name="schema"/> naming <paramref name="kind"/> in <c>x-osdu-schema-source</c> when it names none: what an
    /// import does to a schema of one's own, so the file it keeps in a template folder says which kind it holds, as every
    /// schema of the OSDU data definitions does. A schema that names a kind is left as it is.
    /// </summary>
    internal static JsonObject WithKind(JsonObject schema, string kind)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!DeclaresKind(schema))
        {
            schema["x-osdu-schema-source"] = kind;
        }

        return schema;
    }

    /// <summary>
    /// The kind a schema says it describes, its own <c>x-osdu-schema-source</c>: how a template file in a repository's template
    /// folder names the kind it holds, as every schema of the OSDU data definitions does. The file's name is never read for it.
    /// </summary>
    /// <exception cref="DeliveryException">The schema names no kind there, or names something that is not a record kind.</exception>
    internal static string KindDeclaredBy(JsonObject schema, string where)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema["x-osdu-schema-source"] is not JsonValue declared || !declared.TryGetValue<string>(out var kind) || string.IsNullOrWhiteSpace(kind))
        {
            throw new DeliveryException(
                $"{where}: the schema names no kind. A template file declares the kind it holds in x-osdu-schema-source (authority:source:entityType:major.minor.patch), as every schema of the OSDU data definitions does.");
        }

        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new DeliveryException(
                $"{where}: x-osdu-schema-source '{kind}' is not a record kind; it must be 'authority:source:entityType:major.minor.patch'.");
        }

        return kind;
    }

    /// <summary>A bundled schema as a template is saved from it: it describes the kind, refers to nothing outside itself, and declares <c>data</c>.</summary>
    internal static SchemaSnapshot Validated(string kind, JsonObject bundled, DateTimeOffset capturedUtc, string where)
    {
        if (bundled["x-osdu-schema-source"] is JsonValue declared && declared.TryGetValue<string>(out var source)
            && !string.IsNullOrWhiteSpace(source) && !string.Equals(source, kind, StringComparison.Ordinal))
        {
            throw new DeliveryException($"{where}: the schema describes '{source}', not '{kind}'.");
        }

        var definitions = bundled["definitions"] as JsonObject ?? bundled["$defs"] as JsonObject;
        foreach (var reference in References(bundled))
        {
            var name = reference.StartsWith("#/definitions/", StringComparison.Ordinal)
                ? reference["#/definitions/".Length..]
                : reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? reference["#/$defs/".Length..] : null;
            if (name is null)
            {
                throw new DeliveryException(
                    $"{where}: the schema refers to '{reference}' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.");
            }

            if (definitions?[name] is not JsonObject)
            {
                throw new DeliveryException($"{where}: the schema refers to definition '{name}', which it does not contain.");
            }
        }

        var snapshot = new SchemaSnapshot(kind, bundled, capturedUtc);
        if (snapshot.EffectiveRootObject["properties"] is not JsonObject properties || properties["data"] is null)
        {
            throw new DeliveryException($"{where}: the schema declares no 'data' property, so it does not describe an OSDU record.");
        }

        return snapshot;
    }

    internal static void RequireKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new FlowValidationException($"Kind '{kind}' must be 'authority:source:entityType:major.minor.patch'.");
        }
    }

    private static bool DeclaresData(JsonObject schema, int depth)
    {
        if (schema["properties"] is JsonObject properties && properties["data"] is not null)
        {
            return true;
        }

        // A published schema nests one allOf at most; the bound keeps a hand-made file from walking a deep tree of them.
        return depth < MaxAllOfDepth && schema["allOf"] is JsonArray branches
            && branches.OfType<JsonObject>().Any(branch => DeclaresData(branch, depth + 1));
    }

    private static async Task<JsonObject> ReadCheckoutFileAsync(string root, string path, CancellationToken ct)
    {
        var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            // A checkout that keeps the files in other folders (the osdu-client repository's Specifications/Data) still holds them by name.
            file = Directory.EnumerateFiles(root, Path.GetFileName(file), SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new DeliveryException($"No schema file '{path}' under '{root}'.");
        }

        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                ?? throw new DeliveryException($"Schema file '{file}' is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"Schema file '{file}' is not valid JSON ({ex.Message}).", ex);
        }
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference))
                {
                    yield return reference;
                }

                foreach (var (_, child) in obj)
                {
                    foreach (var inner in References(child))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in References(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}
