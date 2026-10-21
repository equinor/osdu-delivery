using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Templates held in memory, which a suite reads whatever a document's path, where a host reads the template folders on it
/// (osdu/docs/reference/concepts/templates.md, Template folders): a version named is read exactly, and a kind named alone
/// by the version of it added last. A suite of the folders themselves reads the folders, as a host does.
/// </summary>
public sealed class MemoryTemplates : ITemplateSource
{
    private readonly List<SchemaSnapshot> _schemas = [];
    private readonly Lock _gate = new();

    public MemoryTemplates(params SchemaSnapshot[] schemas)
        : this((IEnumerable<SchemaSnapshot>)schemas)
    {
    }

    public MemoryTemplates(IEnumerable<SchemaSnapshot> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        foreach (var schema in schemas)
        {
            Add(schema);
        }
    }

    /// <summary>Holds <paramref name="schema"/> too; a version already held is held once.</summary>
    public MemoryTemplates Add(SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_gate)
        {
            if (!_schemas.Any(s => Same(s, schema.Kind, schema.Version)))
            {
                _schemas.Add(schema);
            }
        }

        return this;
    }

    /// <summary>Holds <paramref name="reference"/> no longer; whether it was held.</summary>
    public bool Remove(TemplateReference reference)
    {
        lock (_gate)
        {
            return _schemas.RemoveAll(s => Same(s, reference.Kind, reference.Version)) > 0;
        }
    }

    /// <summary>The schema of <paramref name="reference"/>, or null when it is not held.</summary>
    public Task<SchemaSnapshot?> LoadAsync(TemplateReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_schemas.FirstOrDefault(s => Same(s, reference.Kind, reference.Version)));
        }
    }

    public Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ct.ThrowIfCancellationRequested();
        SchemaSnapshot? found;
        lock (_gate)
        {
            found = string.IsNullOrWhiteSpace(version)
                ? _schemas.LastOrDefault(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase))
                : _schemas.FirstOrDefault(s => Same(s, kind, version));
        }

        // A template no folder holds is answered as the folders on a document's path answer it: where to put its file.
        return Task.FromResult(found is null
            ? TemplateResolution.Missing(
                $"no template folder holds template {kind}{(string.IsNullOrWhiteSpace(version) ? string.Empty : $" version {version}")}. "
                + $"Put its file in one: 'sqlflow template capture --kind {kind} --out {TemplateFolders.FolderName}/{FileName(kind)}' writes the newest release's.")
            : TemplateResolution.Of(Info(found), found));
    }

    /// <summary>Every template held, by kind, the one added last of a kind first.</summary>
    public Task<IReadOnlyList<TemplateInfo>> ListForAsync(string? document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TemplateInfo>>([.. _schemas
                .Select((s, order) => (Schema: s, Order: order))
                .OrderBy(s => s.Schema.Kind, StringComparer.Ordinal)
                .ThenByDescending(s => s.Order)
                .Select(s => Info(s.Schema))]);
        }
    }

    private static bool Same(SchemaSnapshot schema, string kind, string version)
        => string.Equals(schema.Kind, kind, StringComparison.OrdinalIgnoreCase) && string.Equals(schema.Version, version, StringComparison.OrdinalIgnoreCase);

    private static TemplateInfo Info(SchemaSnapshot schema)
        => new(schema.Kind, schema.Version, $"{TemplateFolders.FolderName}/{FileName(schema.Kind)}");

    /// <summary>The file a template folder keeps a kind in, or a placeholder for a name that is not a record kind.</summary>
    private static string FileName(string kind)
        => Documents.FlowMapper.IsRecordKind(kind) ? TemplateSources.FileName(kind) : "<kind>.json";
}
