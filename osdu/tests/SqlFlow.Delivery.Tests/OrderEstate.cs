using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An estate written for an OSDU order test: delivery flows, sources, cache flows and their mappings in a folder of its own,
/// and schemas made for the case (the relationships a test needs, nothing else) kept in a <see cref="Templates"/> store
/// several estates may share, which a test puts in an estate's template folder with <see cref="Hold"/>.
/// </summary>
internal sealed class OrderEstate
{
    /// <summary>The partition flows name unless a test says otherwise.</summary>
    public const string Partition = "dev";

    public static readonly DateTime Utc = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public OrderEstate(string root, MemoryTemplates templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(templates);
        Root = root;
        Templates = templates;
    }

    public string Root { get; }

    public MemoryTemplates Templates { get; }

    /// <summary>Saves a schema of <paramref name="entityType"/> (version 1.0.0) holding <paramref name="references"/>, each a property of data pointing to an entity type.</summary>
    public void Schema(string entityType, params (string Property, string Target)[] references)
    {
        var kind = $"osdu:wks:{entityType}:1.0.0";
        var data = new JsonObject { ["Name"] = new JsonObject { ["type"] = "string" } };
        foreach (var (property, target) in references)
        {
            var split = target.IndexOf("--", StringComparison.Ordinal);
            data[property] = new JsonObject
            {
                ["type"] = "string",
                ["x-osdu-relationship"] = new JsonArray(new JsonObject { ["GroupType"] = target[..split], ["EntityType"] = target[(split + 2)..] }),
            };
        }

        static JsonObject Strings() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["x-osdu-schema-source"] = kind,
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject { ["type"] = "string" },
                ["acl"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["owners"] = Strings(), ["viewers"] = Strings() } },
                ["legal"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["legaltags"] = Strings(), ["otherRelevantDataCountries"] = Strings() } },
                ["data"] = new JsonObject { ["type"] = "object", ["properties"] = data },
            },
        };
        Templates.Save(TemplateSources.FromBundledJson(schema.ToJsonString(), kind, new DateTimeOffset(Utc), $"{entityType}.json"));
    }

    /// <summary>
    /// Puts the schemas made for the case in the estate's template folder, <c>templates</c> at its top, as a repository holds
    /// the templates its mappings fill; with entity types named, those alone.
    /// </summary>
    public void Hold(params string[] entityTypes)
    {
        var folder = Path.Combine(Root, "templates");
        Directory.CreateDirectory(folder);
        foreach (var schema in Templates.All.Where(s => entityTypes.Length == 0 || entityTypes.Any(e => s.Kind.Contains($":{e}:", StringComparison.Ordinal))))
        {
            File.WriteAllText(Path.Combine(folder, TemplateSources.FileName(schema.Kind)), TemplateSources.FileText(schema));
        }
    }

    /// <summary>
    /// A delivery flow of <paramref name="name"/> writing <paramref name="entityType"/>, its mapping filling the properties named
    /// from columns, <paramref name="searched"/> by a search, <paramref name="refChecked"/> with a ref checked against the
    /// cache, and <paramref name="literals"/> as written. <paramref name="partition"/> null leaves it to the registry.
    /// </summary>
    public void Delivery(
        string name,
        string entityType,
        string[]? columns = null,
        string? partition = Partition,
        string? searched = null,
        (string Property, string Target)? refChecked = null,
        (string Property, string Value)[]? literals = null,
        string? cache = null,
        string? enrich = null)
    {
        WriteMapping(name, entityType, columns ?? [], searched, refChecked, literals);
        Write($"flows/{name}.yaml", $$"""
            flowType: delivery
            name: {{name}}
            {{Partitions(partition)}}
            {{(cache is null ? string.Empty : $"cache: {cache}")}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record:
                object: OsduData.silver.{{Table(name)}}
                key: [name]
                primaryKey: RecId
              work: ../.work/{{name}}
            render:
              mapping: {{Mapping(name)}}@1.0.0
            {{enrich ?? string.Empty}}
            target:
              endpoint: ${env:OSDU_URL}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token:
                  url: ${env:OSDU_TOKEN_URL}
              protocol: storage
            """);
    }

    /// <summary>
    /// A fragment of <paramref name="name"/> over <paramref name="entityType"/>'s template, as a step of <c>enrich:</c> renders
    /// it: it fills the property named from its row, checked against the entity type that property references.
    /// </summary>
    public void Fragment(string name, string entityType, (string Property, string Target) refChecked)
    {
        var kind = $"osdu:wks:{entityType}:1.0.0";
        Write($"mappings/{Mapping(name)}@1.0.0.yaml", $$"""
            documentType: mapping
            name: {{Mapping(name)}}
            version: 1.0.0
            template:
              kind: {{kind}}
              version: {{Templates.VersionOf(kind)}}
            fragment: true
            parameters:
              dataPartition:
                required: true
            record:
              data:
                {{refChecked.Property}}:
                  $from: {{refChecked.Property.ToLowerInvariant()}}
                  $modifiers:
                    - ref: {{refChecked.Target}}
            """);
    }

    /// <summary>A source of <paramref name="name"/> delivering one interface per entry, each filling the property named (if any) from a column.</summary>
    public void Source(string name, params (string Interface, string EntityType, string? Column)[] interfaces)
    {
        var listed = new List<string>();
        foreach (var (face, entityType, column) in interfaces)
        {
            var mapping = $"{name}-{face}";
            WriteMapping(mapping, entityType, column is null ? [] : [column], null, null, null);
            listed.Add($$"""
                  {{face}}:
                    record:
                      object: OsduData.silver.{{Table(mapping)}}
                      key: [name]
                      primaryKey: RecId
                    mapping: {{Mapping(mapping)}}@1.0.0
                """);
        }

        Write($"flows/{name}.yaml", $$"""
            flowType: delivery
            name: {{name}}
            partitions:
              - name: {{Partition}}
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/{{name}}
            interfaces:
            {{string.Join("\n", listed)}}
            target:
              endpoint: ${env:OSDU_URL}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token:
                  url: ${env:OSDU_TOKEN_URL}
            """);
    }

    public void Cache(string name, params string[] entityTypes) => CacheIn(Partition, name, entityTypes);

    /// <summary>A cache flow of <paramref name="name"/> capturing every record of each entity type; <paramref name="partition"/> null leaves it to the registry.</summary>
    public void CacheIn(string? partition, string name, params string[] entityTypes)
    {
        var types = string.Join("\n", entityTypes.Select(e => $$"""
              - kind: "osdu:wks:{{e}}:*"
                name: {{e[(e.IndexOf("--", StringComparison.Ordinal) + 2)..]}}
                fields: [data.Name]
            """));
        Write($"cache/{name}.yaml", $$"""
            flowType: cache
            name: {{name}}
            {{(partition is null ? string.Empty : $"partitions: [{partition}]")}}
            source:
              endpoint: ${env:OSDU_URL}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token:
                  url: ${env:OSDU_TOKEN_URL}
            types:
            {{types}}
            """);
    }

    /// <summary>Removes a flow the estate holds, with its mapping.</summary>
    public void Remove(string name)
    {
        File.Delete(Path.Combine(Root, "flows", $"{name}.yaml"));
        File.Delete(Path.Combine(Root, "mappings", $"{Mapping(name)}@1.0.0.yaml"));
    }

    public void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private void WriteMapping(
        string name, string entityType, string[] columns, string? searched, (string Property, string Target)? refChecked, (string Property, string Value)[]? literals)
    {
        var kind = $"osdu:wks:{entityType}:1.0.0";
        var version = Templates.VersionOf(kind);
        var data = new List<string> { "    Name: { $from: name }" };
        data.AddRange(columns.Select(c => $"    {c}: {{ $from: {c.ToLowerInvariant()} }}"));
        data.AddRange((literals ?? []).Select(l => $"    {l.Property}: {l.Value}"));
        if (refChecked is { } check)
        {
            data.Add($"""
                    {check.Property}:
                      $from: {check.Property.ToLowerInvariant()}
                      $modifiers:
                        - ref: {check.Target}
                """.TrimEnd());
        }

        var searches = string.Empty;
        if (searched is not null)
        {
            var target = SchemaTargetOf(kind, searched);
            data.Add($"""
                    {searched}:
                      $search: Target
                      $findBy:
                        - data.Name = {searched.ToLowerInvariant()}
                """.TrimEnd());
            searches = $"""
                searches:
                  Target:
                    kind: "osdu:wks:{target}:*"
                    schema:
                      kind: osdu:wks:{target}:1.0.0
                      version: {Templates.VersionOf($"osdu:wks:{target}:1.0.0")}

                """;
        }

        Write($"mappings/{Mapping(name)}@1.0.0.yaml", $$"""
            documentType: mapping
            name: {{Mapping(name)}}
            version: 1.0.0
            template:
              kind: {{kind}}
              version: {{version}}
            dataset:
              system: tests
              key: [name]
            parameters:
              dataPartition:
                required: true
            {{searches}}record:
              acl:
                owners: [data.tests.owners@dev.example.com]
                viewers: [data.tests.viewers@dev.example.com]
              legal:
                legaltags: [dev-tests]
                otherRelevantDataCountries: [US]
              data:
            {{string.Join("\n", data)}}
            """);
    }

    private string SchemaTargetOf(string kind, string property)
        => Templates.Find(kind)!.Variables.Single(v => v.Path.Text == $"osdu.data.{property}").Relationships.Single();

    private static string Partitions(string? partition) => partition is null ? string.Empty : $"partitions:\n  - name: {partition}";

    public static string Mapping(string name) => string.Concat(name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static string Table(string name) => Mapping(name);

    /// <summary>
    /// The templates the estate was written with, held in memory by kind and version, as the template folders on a mapping's
    /// path answer a lineage read whatever the mapping's path; a kind forgotten is held by no folder.
    /// </summary>
    public sealed class MemoryTemplates : ITemplateSource
    {
        private readonly Dictionary<string, SchemaSnapshot> _byKind = new(StringComparer.Ordinal);
        private readonly HashSet<string> _forgotten = new(StringComparer.Ordinal);

        /// <summary>Every schema held, in kind order, for a test that also puts them in a repository's template folder.</summary>
        public IReadOnlyList<SchemaSnapshot> All => [.. _byKind.Values.OrderBy(s => s.Kind, StringComparer.Ordinal)];

        public void Save(SchemaSnapshot schema) => _byKind[schema.Kind] = schema;

        public string VersionOf(string kind) => _byKind[kind].Version;

        public OsduTemplate? Find(string kind) => _byKind.TryGetValue(kind, out var schema) ? OsduTemplate.From(schema) : null;

        public void Clear() => _forgotten.UnionWith(_byKind.Keys);

        public void Forget(string entityType) => _forgotten.Add($"osdu:wks:{entityType}:1.0.0");

        public Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(!_forgotten.Contains(kind) && _byKind.TryGetValue(kind, out var schema) && (version is null || schema.Version == version)
                ? TemplateResolution.Of(Info(schema), schema)
                : TemplateResolution.Missing($"no template folder holds template {kind}{(version is null ? string.Empty : $" version {version}")}"));
        }

        /// <summary>Every schema held and not forgotten, as the template folders on a mapping's path list them.</summary>
        public Task<IReadOnlyList<TemplateInfo>> ListForAsync(string? document, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TemplateInfo>>([.. _byKind.Values
                .Where(s => !_forgotten.Contains(s.Kind))
                .OrderBy(s => s.Kind, StringComparer.Ordinal)
                .Select(Info)]);

        private static TemplateInfo Info(SchemaSnapshot schema) => new(schema.Kind, schema.Version, $"templates/{TemplateSources.FileName(schema.Kind)}");
    }
}
