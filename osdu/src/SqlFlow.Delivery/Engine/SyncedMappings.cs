using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Reads a mapping the repository sync kept in the module database, where the engine has no repository tree to read it from:
/// the explorer and an assertion flow judge a mapping's assertions on what OSDU holds this way
/// (osdu/docs/reference/flow/mapping-assertions.md). A mapping is read by its synced id, or by its reference among every
/// repository that declares it; each answer is the mapping, or why it cannot be had, worded for the page or report that
/// shows it.
/// </summary>
public static class SyncedMappings
{
    /// <summary>The mapping synced under <paramref name="id"/>, or why it cannot be read.</summary>
    public static async Task<(MappingDefinition? Mapping, string? Problem)> ByIdAsync(EngineContext context, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Ledger is not { } ledger)
        {
            return (null, "the host keeps no module database, so no synced mapping can be read");
        }

        return await ledger.SyncedMappingAsync(id, ct).ConfigureAwait(false) is { } synced
            ? await ParseAsync(ledger, context.RepositoryFolders, context.Documents, synced, ct).ConfigureAwait(false)
            : (null, $"no mapping is synced under {id:D}; its repository may no longer declare it");
    }

    /// <summary>
    /// The mapping synced under <paramref name="reference"/> (<c>Name@version</c>), or why it cannot be read: none is synced,
    /// or several repositories declare the reference with documents that differ, which leaves no one mapping to read.
    /// </summary>
    public static Task<(MappingDefinition? Mapping, string? Problem)> ByReferenceAsync(EngineContext context, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ByReferenceAsync(context.Ledger, context.RepositoryFolders, context.Documents, reference, ct);
    }

    /// <summary>
    /// The mapping synced under <paramref name="reference"/> in <paramref name="ledger"/>, read by <paramref name="loader"/>,
    /// its template read from its file in the folder <paramref name="folders"/> finds its repository in: what a page reads
    /// where it has the module database and no engine.
    /// </summary>
    public static async Task<(MappingDefinition? Mapping, string? Problem)> ByReferenceAsync(
        ILedger? ledger, Catalog.IRepositoryFolders folders, DeliveryDocumentLoader loader, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (ledger is null)
        {
            return (null, $"the host keeps no module database, so mapping {reference} cannot be read");
        }

        var synced = await ledger.SyncedMappingsAsync(reference, ct).ConfigureAwait(false);
        if (synced.Count == 0)
        {
            return (null, $"no mapping {reference} is synced; sync the repository that declares it, then run again");
        }

        // Two repositories hold one mapping only when they hold the same document and it fills the same template: a mapping
        // that names its template's kind alone fills whatever version the folders on its path hold, which can differ.
        var documents = synced.GroupBy(s => (s.ContentHash, Template: s.TemplateVersion.ToUpperInvariant())).ToList();
        if (documents.Count > 1)
        {
            var differ = synced.GroupBy(s => s.ContentHash, StringComparer.Ordinal).Count() > 1 ? "whose documents differ" : "whose template folders give it different templates";
            return (null,
                $"mapping {reference} is synced from {synced.Select(s => s.RepoId).Distinct().Count()} repositories {differ} "
                + $"({string.Join(", ", documents.Select(d => d.First().RelativePath + (d.Key.Template.Length == 0 ? string.Empty : $" (template {d.First().TemplateVersion})")))}), so there is no one mapping to read; give each its own version");
        }

        return await ParseAsync(ledger, folders, loader, synced[0], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The synced mapping read again, its templates settled from the template folders its repository's last sync recorded on
    /// its path, as the sync settled them, and read from their files (osdu/docs/reference/concepts/templates.md, Template
    /// folders).
    /// </summary>
    private static async Task<(MappingDefinition? Mapping, string? Problem)> ParseAsync(
        ILedger ledger, Catalog.IRepositoryFolders folders, DeliveryDocumentLoader loader, SyncedMapping synced, CancellationToken ct)
    {
        if (!synced.Valid)
        {
            return (null, $"mapping {synced.Reference} ({synced.RelativePath}) does not load: {synced.Message ?? "the sync recorded no reason"}");
        }

        try
        {
            var held = await Catalog.SyncedTemplates.ReadAsync(ledger, folders, synced.RepoId, ct).ConfigureAwait(false);
            return (loader.ParseMapping(synced.Yaml, synced.RelativePath, held.For(synced.RelativePath)), null);
        }
        catch (FlowValidationException ex)
        {
            // The document loaded when it was synced and does not now: this build reads mappings more strictly.
            return (null, $"mapping {synced.Reference} ({synced.RelativePath}) does not load: {ex.Message}");
        }
    }
}
