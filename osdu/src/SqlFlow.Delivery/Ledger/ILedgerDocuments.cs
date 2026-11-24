namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// A mapping the repository sync read into the module database: where it came from, its text (secret references redacted,
/// as synced) and whether it loaded. The engine reads a mapping this way where it has no repository tree.
/// </summary>
/// <param name="Id">The synced mapping's id, derived from its repository and reference.</param>
/// <param name="RepoId">The repository it was read from.</param>
/// <param name="Reference">Its reference, <c>Name@version</c>.</param>
/// <param name="Kind">The kind of the template it fills.</param>
/// <param name="RelativePath">Its file, relative to the repository.</param>
/// <param name="ContentHash">The hash of its text, which tells two repositories' documents of one reference apart.</param>
/// <param name="Yaml">Its text.</param>
/// <param name="Valid">Whether the sync loaded it.</param>
/// <param name="Message">Why the sync could not load it, when it could not.</param>
/// <param name="TemplateVersion">The template version the sync settled it to, from the template folders on its path; empty when none.</param>
public sealed record SyncedMapping(
    Guid Id, Guid RepoId, string Reference, string Kind, string RelativePath, string ContentHash, string Yaml, bool Valid, string? Message, string TemplateVersion = "");

/// <summary>A file of a repository's template folder as the last sync found it.</summary>
/// <param name="Path">The file's path in the repository.</param>
/// <param name="Folder">The template folder it is in, its path in the repository.</param>
/// <param name="Kind">The kind its schema declares; empty when it holds no template.</param>
/// <param name="Version">The template version it holds; empty when it holds none.</param>
/// <param name="Problem">Why it holds no template; null when it holds one.</param>
public sealed record SyncedTemplateFile(string Path, string Folder, string Kind, string Version, string? Problem);

/// <summary>A file of a template folder of any synced repository, as that repository's last sync found it.</summary>
/// <param name="RepoId">The repository that holds it.</param>
/// <param name="Path">The file's path in the repository.</param>
/// <param name="Folder">The template folder it is in, its path in the repository.</param>
/// <param name="Kind">The kind its schema declares; empty when it holds no template.</param>
/// <param name="Version">The template version it holds; empty when it holds none.</param>
/// <param name="Problem">Why it holds no template; null when it holds one.</param>
/// <param name="SeenUtc">When the repository's last sync saw it.</param>
/// <param name="Parent">The kind its template is linked to as its parent; null when it is linked to none.</param>
public sealed record RepositoryTemplateFile(Guid RepoId, string Path, string Folder, string Kind, string Version, string? Problem, DateTime SeenUtc, string? Parent = null);

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds the mappings and templates a repository's sync recorded. A
/// caller that uses only this part takes it rather than the whole ledger; <see cref="OsduLedger"/> implements every
/// part.
/// </summary>
public interface ILedgerDocuments
{
    /// <summary>The mapping the repository sync read under <paramref name="mappingId"/>, or null when none is synced under it.</summary>
    Task<SyncedMapping?> SyncedMappingAsync(Guid mappingId, CancellationToken ct = default);

    /// <summary>
    /// The mappings the repository sync read under <paramref name="reference"/> (<c>Name@version</c>), one per repository that
    /// declares it, at most <c>50</c>; empty when none is synced.
    /// </summary>
    Task<IReadOnlyList<SyncedMapping>> SyncedMappingsAsync(string reference, CancellationToken ct = default);

    /// <summary>
    /// The files of the template folders of repository <paramref name="repoId"/> as its last sync found them
    /// (osdu/docs/reference/concepts/templates.md, Template folders), in path order; empty when it holds none.
    /// </summary>
    Task<IReadOnlyList<SyncedTemplateFile>> RepositoryTemplatesAsync(Guid repoId, CancellationToken ct = default);

    /// <summary>
    /// The files of the template folders of every synced repository as each one's last sync found them, by repository and
    /// path, at most <c>50000</c>: what the explorer and the mapping builder read templates from where no document asks.
    /// </summary>
    Task<IReadOnlyList<RepositoryTemplateFile>> RepositoryTemplatesAsync(CancellationToken ct = default);
}
