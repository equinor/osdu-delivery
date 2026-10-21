using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>The identity a mapping pins a template by: the OSDU kind and the content version of the saved schema.</summary>
public readonly record struct TemplateReference(string Kind, string Version)
{
    public override string ToString() => $"{Kind} version {Version}";
}

/// <summary>A saved template version as listings show it.</summary>
public sealed record TemplateInfo(string Kind, string Version, DateTime CapturedUtc, string CapturedBy, string Origin)
{
    public TemplateReference Reference => new(Kind, Version);
}

/// <summary>What saving a template did.</summary>
public enum TemplateSaveOutcome
{
    /// <summary>The schema is a new template version.</summary>
    Created,

    /// <summary>Exactly this template version was already saved.</summary>
    Unchanged,
}

public sealed record TemplateSaved(TemplateInfo Template, TemplateSaveOutcome Outcome);

/// <summary>
/// The saved templates, the library (osdu/docs/reference/concepts/templates.md): OSDU schemas saved in the module's
/// database from the Templates page or the command line, each version immutable and identified by its kind and content
/// version, for what reads templates where no repository is at hand (the explorer, the mapping builder, the assistant).
/// It is not linked to the repositories: a repository's documents read their templates from their template folders,
/// never from here, the sync saves nothing here, and nothing a repository holds keeps a version from being deleted. As a
/// source, the library settles a version exactly and a kind alone by the newest saved.
/// </summary>
public interface ITemplateStore : ITemplateSource
{
    Task<TemplateResolution> ITemplateSource.ResolveAsync(string? document, string kind, string? version, CancellationToken ct)
        => TemplateLibrary.ResolveAsync(this, kind, version, ct);

    Task<IReadOnlyList<TemplateInfo>> ITemplateSource.ListForAsync(string? document, CancellationToken ct)
        => ListAsync(ct);

    /// <summary>The schema of a saved template version, or null when that version is not saved.</summary>
    Task<SchemaSnapshot?> LoadAsync(TemplateReference reference, CancellationToken ct = default);

    /// <summary>Saves a schema as a template version. Saving a version that is already saved changes nothing.</summary>
    Task<TemplateSaved> SaveAsync(SchemaSnapshot schema, string origin, string actor, CancellationToken ct = default);

    /// <summary>Every saved template version, by kind and then newest first.</summary>
    Task<IReadOnlyList<TemplateInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>Deletes a saved template version. No repository document reads it, so nothing refuses it.</summary>
    Task DeleteAsync(TemplateReference reference, CancellationToken ct = default);
}

/// <summary>The template store over the <c>osdu.Template</c> table.</summary>
public sealed class OsduTemplateStore : ITemplateStore
{
    private static readonly Guid TemplateNamespace = DeterministicGuid.Namespace("delivery-template");

    private readonly Func<OsduDbContext> _factory;
    private readonly TimeProvider _time;

    // A saved version never changes, so a parsed schema is reused for as long as the process lives. The key is the
    // reference, which includes the content version, so a deleted and re-saved version is the same content by definition.
    private readonly ConcurrentDictionary<TemplateReference, SchemaSnapshot> _loaded = new();

    public OsduTemplateStore(Func<OsduDbContext> factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The stable row id of a template version.</summary>
    public static Guid IdOf(TemplateReference reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Version);
        return DeterministicGuid.V5(TemplateNamespace, reference.Kind + "@" + reference.Version);
    }

    public async Task<SchemaSnapshot?> LoadAsync(TemplateReference reference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Version);
        if (_loaded.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        await using var db = _factory();
        var row = await db.DeliveryTemplates.AsNoTracking()
            .Where(t => t.Kind == reference.Kind && t.Version == reference.Version)
            .Select(t => new { t.SchemaJson, t.CapturedUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var schema = SchemaSnapshot.Parse(reference.Kind, row.SchemaJson, new DateTimeOffset(DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc)));
        if (!string.Equals(schema.Version, reference.Version, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"The template {reference} is damaged: its stored schema hashes to version {schema.Version}. Delete that version and save the schema again.");
        }

        _loaded[reference] = schema;
        return schema;
    }

    public async Task<TemplateSaved> SaveAsync(SchemaSnapshot schema, string origin, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var reference = new TemplateReference(schema.Kind, schema.Version);

        await using (var db = _factory())
        {
            var existing = await FindAsync(db, reference, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return new TemplateSaved(Info(existing), TemplateSaveOutcome.Unchanged);
            }

            var row = new DeliveryTemplate
            {
                Id = IdOf(reference),
                Kind = schema.Kind,
                Version = schema.Version,
                // Stored in the order the schema was written, so it reads back, and lays out its variables, as authored.
                // The version is the hash of the canonical form, so the order does not move it.
                SchemaJson = DocumentJson.Compact(schema.Root),
                Origin = Bounded(origin, 1000),
                CapturedBy = Bounded(actor, 200),
                CapturedUtc = _time.GetUtcNow().UtcDateTime,
            };
            db.DeliveryTemplates.Add(row);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _loaded[reference] = schema;
                return new TemplateSaved(Info(row), TemplateSaveOutcome.Created);
            }
            catch (DbUpdateException)
            {
                // A concurrent save of the same version won the race; the version is content-addressed, so what it stored
                // is this schema, and the read below reports it.
            }
        }

        await using var reread = _factory();
        var winner = await FindAsync(reread, reference, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"The template {reference} could not be saved, and the catalog does not hold it.");
        return new TemplateSaved(Info(winner), TemplateSaveOutcome.Unchanged);
    }

    public async Task<IReadOnlyList<TemplateInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = _factory();
        var rows = await db.DeliveryTemplates.AsNoTracking()
            .Select(t => new { t.Kind, t.Version, t.CapturedUtc, t.CapturedBy, t.Origin })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .OrderBy(t => t.Kind, StringComparer.Ordinal)
            .ThenByDescending(t => t.CapturedUtc)
            .Select(t => new TemplateInfo(t.Kind, t.Version, t.CapturedUtc, t.CapturedBy, t.Origin))
            .ToList();
    }

    public async Task DeleteAsync(TemplateReference reference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Version);
        await using var db = _factory();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        var row = await FindAsync(db, reference, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"There is no template {reference}.");
        db.DeliveryTemplates.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _loaded.TryRemove(reference, out _);
    }

    private static Task<DeliveryTemplate?> FindAsync(OsduDbContext db, TemplateReference reference, CancellationToken ct)
        => db.DeliveryTemplates.FirstOrDefaultAsync(t => t.Kind == reference.Kind && t.Version == reference.Version, ct);

    private static TemplateInfo Info(DeliveryTemplate row) => new(row.Kind, row.Version, row.CapturedUtc, row.CapturedBy, row.Origin);

    private static string Bounded(string value, int max) => value.Length <= max ? value : value[..max];
}
