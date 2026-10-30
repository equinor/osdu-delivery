using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// What a flow that keeps a ledger is apart from its name and the file it is written in (osdu/docs/reference/concepts/
/// renaming-a-pipeline.md): SHA-256, in lowercase hex. Two flows with one hash are one flow under two names, which is how
/// the repository sync tells a renamed flow from a new one. Comments and layout play no part either way.
/// <list type="bullet">
/// <item>A delivery flow is hashed as parsed, with its name and path left out, together with the mapping document each
/// interface pins, by the content hash the repository sync recorded of it, since what renders a delivery is part of what
/// it is.</item>
/// <item>A flow of another kind (retrieval, assertion, dimension, inventory, download) is hashed as its YAML document reads, its
/// top-level <c>name</c> left out and every mapping's keys taken in order: what such a flow derives from its name as it is
/// parsed (a dimension view's SQL names the flow that writes it) would otherwise make two names two documents.</item>
/// </list>
/// </summary>
public static class DeliveryDocumentHash
{
    /// <summary>The length of a hash: 64 hex digits.</summary>
    public const int Length = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LeaveOutNoWorkflows, LeaveOutTheKindsOperation } },
    };

    /// <summary>
    /// A flow that names no operation code of its own (<see cref="FlowDefinition.TelemetryOperation"/> the kind's,
    /// <see cref="FlowTelemetry.Delivery"/>) hashes as one written before <c>telemetry:</c> existed, so the hash every such flow
    /// was synced with stays what it was; a flow that names one is another document.
    /// </summary>
    private static void LeaveOutTheKindsOperation(JsonTypeInfo info)
    {
        if (info.Type != typeof(FlowDefinition))
        {
            return;
        }

        foreach (var property in info.Properties.Where(p => p.Name == "telemetryOperation"))
        {
            property.ShouldSerialize = (_, value) => !string.Equals(value as string, FlowTelemetry.Delivery, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A flow that runs no workflow over its mapping's enrichments (<see cref="FlowDefinition.EnrichWorkflows"/> empty) hashes as
    /// one written before <c>workflows:</c> existed, so the hash every such flow was synced with, which the sync tells a renamed
    /// flow by, stays what it was.
    /// </summary>
    private static void LeaveOutNoWorkflows(JsonTypeInfo info)
    {
        if (info.Type != typeof(FlowDefinition))
        {
            return;
        }

        foreach (var property in info.Properties.Where(p => p.Name == "enrichWorkflows"))
        {
            property.ShouldSerialize = (_, value) => value is IReadOnlyDictionary<string, WorkflowRoute> { Count: > 0 };
        }
    }

    /// <summary>
    /// The hash of <paramref name="source"/> with its name and path left out, and of the mapping each interface pins, by its
    /// reference in <paramref name="mappings"/> (the content hash of each mapping document the repository holds); a mapping
    /// the repository does not hold counts as none. What a mapping adds to its records (its <c>enrich:</c> block) is the
    /// mapping's content, and what each enrichment mapping renders enters every record's render context.
    /// </summary>
    public static string Of(SourceDefinition source, IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mappings);
        return Hash(new
        {
            source.ParallelInterfaces,
            Interfaces = source.Interfaces.Select(f => f with { Name = string.Empty, SourcePath = null }).ToList(),
            Mappings = source.Interfaces.Select(f => mappings.GetValueOrDefault(f.Render.Mapping)).ToList(),
        });
    }

    /// <summary>
    /// The hash of a flow document of another kind than delivery, as its YAML reads: every mapping written with its keys in
    /// order and every scalar as it is written, its top-level <c>name</c> left out. Comments, layout and the order keys are
    /// written in play no part.
    /// </summary>
    /// <exception cref="FlowValidationException">The text is no YAML document whose root is a mapping.</exception>
    public static string OfDocument(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new FlowValidationException($"The flow document is not YAML a hash can be taken of: {ex.Message}");
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FlowValidationException("The flow document holds no mapping at its root, so no hash can be taken of it.");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Canonical(root, topLevel: true)));
    }

    /// <summary><paramref name="node"/> as canonical JSON: a mapping's keys in ordinal order, the top-level name left out.</summary>
    private static byte[] Canonical(YamlNode node, bool topLevel)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, node, topLevel);
        }

        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node, bool topLevel)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach (var (key, value) in mapping.Children
                    .Select(c => (Key: KeyOf(c.Key), c.Value))
                    .Where(c => !(topLevel && c.Key == "name"))
                    .OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, value, topLevel: false);
                }

                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                {
                    Write(writer, item, topLevel: false);
                }

                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                writer.WriteStringValue(scalar.Value);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>A mapping key as text: a scalar's value, any other node as its canonical JSON.</summary>
    private static string KeyOf(YamlNode key)
        => key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : System.Text.Encoding.UTF8.GetString(Canonical(key, topLevel: false));

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
}
