using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Hashing;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// The three non-source inputs pinned together for a render (osdu/docs/reference/concepts/change-detection.md,
/// Render-affecting versus operational), plus the mapping parameter values the flow supplied and, for a mapping that
/// searches, the partition's system properties its lookups were written under. Recorded in the ledger against every
/// document and part of every content hash.
/// </summary>
public sealed partial record RenderContext
{
    /// <summary>Well-known parameter every mapping receives: the OSDU data partition ids are minted in.</summary>
    public const string DataPartitionParameter = "dataPartition";

    public required string MappingReference { get; init; }

    /// <summary>The mapping document's fingerprint, so an edit in place re-renders its records; null for a mapping built in code.</summary>
    public string? MappingFingerprint { get; init; }

    /// <summary>The partition whose cache the render read; null when the mapping reads no cache.</summary>
    public string? CacheScope { get; init; }

    /// <summary>The version of the cache the render read; <c>none</c> when it read no cache.</summary>
    public required string CacheVersion { get; init; }

    public required string SchemaSnapshotVersion { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The partition's system properties the render's searches were written under, by state alone
    /// (<see cref="Snapshots.SystemProperties.Pinned"/>); none when the mapping declares no searches, which leaves the
    /// context of every other mapping as it always was. They pin a mapping that only searches in place of a cache
    /// version: a search reads nothing else of the cache, so a capture that changes reference data renders none of its
    /// records again, and one that changes how the partition is searched renders all of them again.
    /// </summary>
    public IReadOnlyList<SystemProperty> SystemProperties { get; init; } = [];

    /// <summary>
    /// The steps of the flow's <c>enrich:</c> the record is rendered with (osdu/docs/reference/flow/enrich.md), in the order they
    /// render: each fragment's reference, fingerprint, template version and parameters, so editing a step, adding one, removing
    /// one or reordering them renders the flow's records again. Empty for a flow that declares none, which leaves its context as
    /// it always was.
    /// </summary>
    public IReadOnlyList<RenderStep> Steps { get; init; } = [];

    /// <summary>
    /// Whether a search that finds nothing exactly asks again regardless of case: the pinned properties say the
    /// partition's indexer keeps a lowercased keyword of text (<see cref="Snapshots.SystemProperties.KeywordLowerOn"/>).
    /// </summary>
    public bool KeywordLower => Snapshots.SystemProperties.KeywordLowerOn(SystemProperties);

    /// <summary>
    /// The partition every record id is minted in. It is the first segment of the id, and the storage service
    /// holds ids to <c>^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+$</c> (openapi storage v2, Record.id), so a
    /// partition carrying anything else would mint an id the service refuses on every record of the run. Saying so
    /// here costs one check and turns a whole failed run into one legible message.
    /// </summary>
    public string DataPartition
    {
        get
        {
            if (!Parameters.TryGetValue(DataPartitionParameter, out var partition) || string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException($"The mapping parameter '{DataPartitionParameter}' is required and must be supplied by the flow under render.parameters.");
            }

            if (!IdSegment().IsMatch(partition))
            {
                throw new FlowValidationException(
                    $"The mapping parameter '{DataPartitionParameter}' is '{partition}', which is not a valid OSDU id segment (letters, digits, underscore, hyphen and dot). Record ids are minted as {{partition}}:{{entityType}}:{{key}} and the storage service would refuse every one of them.");
            }

            return partition;
        }
    }

    [GeneratedRegex(@"^[\w\-\.]+$")]
    private static partial Regex IdSegment();

    /// <summary>Canonical JSON form, stored verbatim in the ledger and fed into hashes.</summary>
    public string Canonical() => CanonicalJson.ToString(Node(withCacheVersion: true));

    public string Hash() => ContentHash.Of(Canonical());

    /// <summary>
    /// The hash of everything but the cache version: the mapping, the template, the parameters and the pinned system
    /// properties. A scope planned under other rules is read whole on its next run; a moved cache version is not, since the
    /// cache rollout redelivers exactly the records a cache change reaches.
    /// </summary>
    public string RulesHash() => ContentHash.Of(CanonicalJson.ToString(Node(withCacheVersion: false)));

    private JsonObject Node(bool withCacheVersion)
    {
        var parameters = new JsonObject();
        foreach (var kv in Parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            parameters[kv.Key] = kv.Value;
        }

        var node = new JsonObject
        {
            ["mapping"] = MappingReference,
            ["schema"] = SchemaSnapshotVersion,
            ["parameters"] = parameters,
        };
        if (withCacheVersion)
        {
            node["cacheVersion"] = CacheVersion;
        }

        if (MappingFingerprint is not null)
        {
            node["mappingFingerprint"] = MappingFingerprint;
        }

        if (CacheScope is not null)
        {
            node["cache"] = CacheScope;
        }

        if (SystemProperties.Count > 0)
        {
            var services = new JsonObject();
            foreach (var property in SystemProperties)
            {
                if (services[property.Service] is not JsonObject names)
                {
                    names = new JsonObject();
                    services[property.Service] = names;
                }

                names[property.Name] = property.State.ToString();
            }

            node["systemProperties"] = services;
        }

        if (Steps.Count > 0)
        {
            node["enrich"] = new JsonArray(Steps.Select(step => (JsonNode?)step.Node()).ToArray());
        }

        return node;
    }

    public static RenderContext Parse(string canonical)
    {
        var node = JsonNode.Parse(canonical) as JsonObject ?? throw new DeliveryException("Render context is not a JSON object.");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node["parameters"] is JsonObject p)
        {
            foreach (var kv in p)
            {
                parameters[kv.Key] = kv.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        return new RenderContext
        {
            MappingReference = node["mapping"]?.GetValue<string>() ?? string.Empty,
            MappingFingerprint = node["mappingFingerprint"]?.GetValue<string>(),
            CacheScope = node["cache"]?.GetValue<string>(),
            CacheVersion = node["cacheVersion"]?.GetValue<string>() ?? string.Empty,
            SchemaSnapshotVersion = node["schema"]?.GetValue<string>() ?? string.Empty,
            Parameters = parameters,
            SystemProperties = ParseSystemProperties(node["systemProperties"]),
            Steps = ParseSteps(node["enrich"]),
        };
    }

    /// <summary>The steps of a canonical context, in the order they render; a step that cannot be read is refused rather than guessed.</summary>
    private static IReadOnlyList<RenderStep> ParseSteps(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray steps)
        {
            throw new DeliveryException("Render context enrich is not a JSON array of steps.");
        }

        return steps.Select((step, i) => RenderStep.Parse(step, i)).ToList();
    }

    /// <summary>The pinned system properties of a canonical context; a state the context does not name is refused rather than guessed.</summary>
    private static IReadOnlyList<SystemProperty> ParseSystemProperties(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonObject services)
        {
            throw new DeliveryException("Render context systemProperties is not a JSON object of services.");
        }

        var properties = new List<SystemProperty>();
        foreach (var (service, names) in services)
        {
            if (names is not JsonObject named)
            {
                throw new DeliveryException($"Render context systemProperties.{service} is not a JSON object of properties.");
            }

            foreach (var (name, value) in named)
            {
                var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (Snapshots.SystemProperties.ParseState(text) is not { } state)
                {
                    throw new DeliveryException($"Render context pins system property {service} {name} to {value?.ToJsonString() ?? "null"}, which is not a state (Enabled, Disabled or Unknown).");
                }

                properties.Add(new SystemProperty(service, name, state, null, null));
            }
        }

        return Snapshots.SystemProperties.Ordered(properties);
    }

    public override string ToString() => Canonical();

    /// <summary>
    /// The context a step's fragment renders under: the flow's cache, system properties and partition, with the fragment's own
    /// reference, fingerprint, template version and parameters. It is the fragment's renderer's; the record's context names
    /// every step (<see cref="Steps"/>).
    /// </summary>
    public RenderContext ForStep(RenderStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return this with
        {
            MappingReference = step.MappingReference ?? throw new ArgumentException($"Step '{step.Name}' renders no fragment of its own, so it has no context of its own.", nameof(step)),
            MappingFingerprint = step.MappingFingerprint,
            SchemaSnapshotVersion = step.SchemaSnapshotVersion,
            Parameters = step.Parameters,
            Steps = [],
        };
    }
}

/// <summary>
/// One step of the flow's <c>enrich:</c> as a record's render context names it (osdu/docs/reference/flow/enrich.md): what
/// rendered the step's fragment, so the ledger says what rendered every part of a record's document.
/// </summary>
/// <param name="Name">The step's name.</param>
/// <param name="MappingReference">The fragment, <c>Name@version</c>; null for a step that registers files alone.</param>
/// <param name="MappingFingerprint">The fragment document's fingerprint; null for one built in code.</param>
/// <param name="SchemaSnapshotVersion">The version of the template the fragment fills.</param>
/// <param name="Parameters">The fragment's parameters as they resolved.</param>
public sealed record RenderStep(
    string Name, string? MappingReference, string? MappingFingerprint, string SchemaSnapshotVersion, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>
    /// For a step that registers files, what decides its datasets as canonical JSON: their kind, key and link, and the fragment
    /// rendered into them with its fingerprint, template version and parameters; null for a step that registers none.
    /// </summary>
    public string? Files { get; init; }

    /// <summary>
    /// True for a step set aside when it holds (<c>onHold: step</c>), whose last delivered contribution a record keeps; a step
    /// whose hold holds the record is written as it always was.
    /// </summary>
    public bool SetAsideOnHold { get; init; }

    /// <summary>True for a step that supplies the record's bulk data (osdu/docs/reference/flow/enrich-bulk.md), named by its one row.</summary>
    public bool SuppliesBulk { get; init; }

    /// <summary>True for a step that runs a workflow over the record (osdu/docs/reference/flow/enrich-workflow.md).</summary>
    public bool RunsWorkflow { get; init; }

    internal JsonObject Node()
    {
        var parameters = new JsonObject();
        foreach (var kv in Parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            parameters[kv.Key] = kv.Value;
        }

        var node = new JsonObject
        {
            ["name"] = Name,
            ["schema"] = SchemaSnapshotVersion,
            ["parameters"] = parameters,
        };
        if (MappingReference is not null)
        {
            node["mapping"] = MappingReference;
        }

        if (MappingFingerprint is not null)
        {
            node["mappingFingerprint"] = MappingFingerprint;
        }

        if (Files is not null)
        {
            node["files"] = JsonNode.Parse(Files);
        }

        if (SetAsideOnHold)
        {
            node["onHold"] = "step";
        }

        if (SuppliesBulk)
        {
            node["bulk"] = true;
        }

        if (RunsWorkflow)
        {
            node["workflow"] = true;
        }

        return node;
    }

    internal static RenderStep Parse(JsonNode? node, int index)
    {
        if (node is not JsonObject step
            || step["name"]?.GetValue<string>() is not { Length: > 0 } name
            || (step["mapping"] is null && step["files"] is null && step["bulk"] is null && step["workflow"] is null))
        {
            throw new DeliveryException($"Render context enrich[{index}] is not a step with a name and a mapping, files, bulk data or a workflow.");
        }

        var mapping = step["mapping"]?.GetValue<string>();

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (step["parameters"] is JsonObject declared)
        {
            foreach (var kv in declared)
            {
                parameters[kv.Key] = kv.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        return new RenderStep(name, mapping, step["mappingFingerprint"]?.GetValue<string>(), step["schema"]?.GetValue<string>() ?? string.Empty, parameters)
        {
            Files = step["files"] is JsonObject files ? CanonicalJson.ToString(files) : null,
            SetAsideOnHold = step["onHold"]?.GetValue<string>() == "step",
            SuppliesBulk = step["bulk"]?.GetValue<bool>() == true,
            RunsWorkflow = step["workflow"]?.GetValue<bool>() == true,
        };
    }
}
