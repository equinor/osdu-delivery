using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a mapping reads from the cache of the partition it renders in, as lineage shows it
/// (osdu/docs/reference/concepts/lineage.md, The mapping is a node between the cache and the flow). A mapping reads the
/// cache two ways. It reads a type by name: a cache source, a lookup, a replace's table, a token of an id
/// (<see cref="MappingDefinition.CacheTypesRead"/>). And it reads records by what they are: every id an <c>id</c> or a
/// <c>ref</c> modifier builds is looked for among the cached records of the entity type it names, in every type of the
/// cache holding that entity type, whatever the type is called. Lineage shows both, so a cache type a mapping only ever
/// checks its references against is not left without a reader.
/// </summary>
/// <param name="Types">The cached types read by name, in name order.</param>
/// <param name="EntityTypes">The entity types whose cached records the ids the mapping builds are looked for among, in order.</param>
/// <param name="Untold">
/// The template variables filled by a <c>ref</c> whose entity type only the mapping's template tells, when no template was
/// at hand to tell it: what they are checked against is not known here. In the order the mapping writes them.
/// </param>
public sealed record MappingCacheReads(IReadOnlyList<string> Types, IReadOnlyList<string> EntityTypes, IReadOnlyList<string> Untold)
{
    /// <summary>
    /// What <paramref name="mapping"/> reads. <paramref name="schema"/> is the template the mapping pins, which says which
    /// entity type a <c>ref</c> written without one references; without it, a <c>ref</c> that writes its entity type in
    /// full and an <c>id</c> whose template writes it as text are still known, and the rest are <see cref="Untold"/>.
    /// </summary>
    /// <remarks>
    /// An id whose entity type a token gives (<c>{$param.dataPartition}:{group}--{entity}:{$value}:</c>) is of a type
    /// only a render knows, and is left out. A <c>ref</c> the template cannot settle (no relationship, or several) is the
    /// preflight's to report, which refuses the mapping; lineage shows nothing for it.
    /// </remarks>
    public static MappingCacheReads Of(MappingDefinition mapping, SchemaSnapshot? schema)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var entityTypes = new SortedSet<string>(StringComparer.Ordinal);
        var untold = new List<string>();
        foreach (var node in mapping.Entries.SelectMany(e => e.ValueNodes))
        {
            // The ids a node builds are its own modifiers' and, for a node reading every row a lookup's record names, the
            // lookup's: the same modifiers the cached types read by name are counted over.
            foreach (var modifier in node.Modifiers.Concat(node.FindAll?.Operand.Lookup?.Modifiers ?? []))
            {
                if (modifier is { Kind: ModifierKind.Id, Id.EntityType: { } built })
                {
                    entityTypes.Add(built);
                }
            }

            if (node.Modifiers.LastOrDefault(m => m.Kind == ModifierKind.Ref) is not { } reference)
            {
                continue;
            }

            if (reference.Id is { EntityType: { } written })
            {
                entityTypes.Add(written);
            }
            else if (schema is null)
            {
                if (!untold.Contains(node.Target.Text, StringComparer.Ordinal))
                {
                    untold.Add(node.Target.Text);
                }
            }
            else if (MappingRenderer.ResolveReference(node, reference, schema).Template is { EntityType: { } resolved })
            {
                entityTypes.Add(resolved);
            }
        }

        return new MappingCacheReads(mapping.CacheTypesRead(), entityTypes.ToList(), untold);
    }
}

/// <summary>How a read of the template a mapping fills came out.</summary>
public enum MappingTemplateOutcome
{
    /// <summary>The template was found, and its schema is at hand.</summary>
    Found,

    /// <summary>No template folder the mapping reads holds it; <see cref="MappingTemplateRead.Reason"/> says where it looked.</summary>
    NotFound,

    /// <summary>The read failed; <see cref="MappingTemplateRead.Reason"/> says why.</summary>
    Failed,
}

/// <summary>The template a mapping fills as lineage read it: its schema, or why there is none.</summary>
/// <param name="Outcome">How the read came out.</param>
/// <param name="Schema">The template's schema, when <see cref="MappingTemplateOutcome.Found"/>.</param>
/// <param name="Reason">Why there is none, redacted, when it was not found or the read failed.</param>
public sealed record MappingTemplateRead(MappingTemplateOutcome Outcome, SchemaSnapshot? Schema = null, string? Reason = null)
{
    /// <summary>Why lineage has no template to read a mapping's references by, as a clause a warning ends with; null when it has one.</summary>
    public string? Missing => Outcome == MappingTemplateOutcome.Found ? null : Reason ?? "it was not read";
}

/// <summary>
/// Reads the templates mappings fill, for lineage: a <c>ref</c> written without its entity type takes it from the template
/// variable it fills, so lineage reads the template from the template folders on the mapping's path, up to the top of the
/// checkout it scans (osdu/docs/reference/concepts/templates.md, Template folders), exactly as a run reads it. A template
/// that is not there and a file that cannot be read each come back as a reason, never as an exception: lineage then shows
/// what the documents alone tell, and says what it left out.
/// </summary>
public sealed class MappingTemplateSource
{
    private readonly ITemplateSource? _source;

    /// <summary>The template folders on each mapping's path: what every host's lineage reads.</summary>
    public MappingTemplateSource()
    {
    }

    /// <summary>A source of fixed templates, read whatever a mapping's path: what a suite describing lineage over given templates composes.</summary>
    public MappingTemplateSource(ITemplateSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>
    /// The template <paramref name="mapping"/> fills, read from the template folders on its path up to
    /// <paramref name="root"/>, the top of the checkout lineage reads; or why there is none to read.
    /// </summary>
    public MappingTemplateRead Read(MappingDefinition mapping, string? root)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        try
        {
            var source = _source ?? new FolderTemplates(root);
            // Describing lineage is synchronous and a source is not; the read runs on the pool so no caller's
            // synchronization context is waited on from inside itself.
            var resolved = Task.Run(() => source.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version))
                .GetAwaiter().GetResult();
            return resolved.Schema is { } schema
                ? new MappingTemplateRead(MappingTemplateOutcome.Found, schema)
                : new MappingTemplateRead(MappingTemplateOutcome.NotFound, Reason: SecretHygiene.RedactedMessage(resolved.Problem ?? "it was not found"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MappingTemplateRead(
                MappingTemplateOutcome.Failed, Reason: $"it could not be read ({ex.GetType().Name}: {SecretHygiene.RedactedMessage(ex.Message)})");
        }
    }
}
