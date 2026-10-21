using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>
/// A search term as people see it (osdu/docs/reference/concepts/search-terms.md): the column it names, the name it is
/// searched by, whether it is left out, the routes it reaches the record by and the one it is searched through, and why
/// it cannot be searched where it cannot. A refinement whose term no mapping gives any longer is one with no routes
/// (<see cref="Orphan"/>).
/// </summary>
public sealed record SearchTermView
{
    public required Guid Id { get; init; }

    /// <summary>The term's key as text (<see cref="SearchTermKey.Text"/>).</summary>
    public required string Key { get; init; }

    /// <summary>The table the column is read from (<c>OsduData.arc.WellLog</c>); empty for a refinement made before terms named theirs.</summary>
    public required string Source { get; init; }

    /// <summary>The table's own name (<c>WellLog</c>).</summary>
    public required string Table { get; init; }

    /// <summary>The entity type the view is of: the one the routes listed fill.</summary>
    public required string EntityType { get; init; }

    public required string Column { get; init; }

    /// <summary>The column as a person who knows the source names it, the table before it: <c>WellLog.wellbore_uwi</c>.</summary>
    public required string ColumnLabel { get; init; }

    /// <summary>The source systems (<c>dataset.system</c>) of the mappings that read the column, each once.</summary>
    public IReadOnlyList<string> Systems { get; init; } = [];

    /// <summary>The name the term is searched by: the one a person gave it, else its column's.</summary>
    public required string Name { get; init; }

    public bool Renamed { get; init; }

    public bool Excluded { get; init; }

    public string? Note { get; init; }

    /// <summary>The route a person picked; null when the term is searched through the one the explorer prefers.</summary>
    public string? PickedRoute { get; init; }

    /// <summary>The route the term is searched through; null when none can be searched.</summary>
    public string? Route { get; init; }

    public IReadOnlyList<SearchRouteView> Routes { get; init; } = [];

    /// <summary>The active delivery flows whose mappings read the column.</summary>
    public IReadOnlyList<string> Flows { get; init; } = [];

    /// <summary>The mappings that read the column, each once.</summary>
    public IReadOnlyList<string> Mappings { get; init; } = [];

    /// <summary>Why the term cannot be searched; null when it can (and is not left out).</summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Where the values to pick a value from are read: the property, and for a route through other records their kind, whose
    /// values are the source's own; null for a route whose written values are not the ones a person types.
    /// </summary>
    public SearchTermSuggest? Suggest { get; init; }

    /// <summary>True for a refinement whose term no mapping of an active flow gives any longer.</summary>
    public bool Orphan { get; init; }

    public string? UpdatedBy { get; init; }

    public DateTime? UpdatedUtc { get; init; }
}

/// <summary>Where the values held for a term are read, to pick one from: a property of a kind, as the platform indexes it.</summary>
public sealed record SearchTermSuggest(string? Kind, string Path, string Index, string? Nested);

/// <summary>A route of a term as people see it: how the column reaches the record, how that is indexed, and what it can be asked.</summary>
public sealed record SearchRouteView
{
    public required string Id { get; init; }

    /// <summary>The entity type the route fills.</summary>
    public required string EntityType { get; init; }

    /// <summary>The kinds the route fills, one per mapping version that writes it (<c>osdu:wks:work-product-component--WellLog:1.5.0</c>).</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>The child dataset the mapping reads the column under (<c>curves</c>); null for the record's own row.</summary>
    public string? Dataset { get; init; }

    public required string Target { get; init; }

    public required string Path { get; init; }

    /// <summary><c>copy</c>, <c>steps</c>, <c>lookup</c>, <c>search</c>, <c>key</c> or <c>expression</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>How the route reaches the record, in words.</summary>
    public required string How { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];

    public IReadOnlyList<string> Mappings { get; init; } = [];

    public string? Location { get; init; }

    public SearchRouteFind? Find { get; init; }

    public IReadOnlyList<string>? KeyColumns { get; init; }

    public string? When { get; init; }

    public string? Description { get; init; }

    /// <summary>How the platform indexes the property compared: text, keyword, number, boolean or date; null when it cannot be searched.</summary>
    public string? Index { get; init; }

    public string? Nested { get; init; }

    /// <summary>The conditions the route takes, the likeliest first, named as the explorer names them.</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];

    /// <summary>Why no condition can be asked through the route; null when one can.</summary>
    public string? Problem { get; init; }
}

/// <summary>What a person makes of a term: a name (null for the column's own), whether it is deleted, the route (null for the preferred one), a note.</summary>
public sealed record SearchTermRefinementRequest(string? Name, bool Excluded, string? Route, string? Note);

/// <summary>
/// What deleting search terms did: the terms taken out of the search (a pipeline still reads each, so the next sync extracts
/// it again; it is listed as deleted until restored), the terms whose refinement was removed for good (no pipeline reads
/// them any longer, so the refinement was all there was of them), and the ids that were neither, already gone.
/// </summary>
public sealed record SearchTermDeletion(IReadOnlyList<Guid> Deleted, IReadOnlyList<Guid> Removed, IReadOnlyList<Guid> Missing);

/// <summary>What restoring search terms did: the terms offered in the explorer again, and the ids no pipeline reads, which have nothing to restore.</summary>
public sealed record SearchTermRestoration(IReadOnlyList<Guid> Restored, IReadOnlyList<Guid> Missing);

/// <summary>
/// The search terms of the module (osdu/docs/reference/concepts/search-terms.md): the terms the repository syncs
/// extracted, merged across repositories, with what people made of them, each route classified against the saved
/// templates; the refinements people make; and a condition on a term turned into the condition the explorer asks of the
/// records. One directory serves one request: it keeps the mappings and templates it read for the rest of it.
/// </summary>
public sealed class SearchTermDirectory
{
    /// <summary>The most terms one deletion or restoration names: every term of an entity type, with room to spare.</summary>
    public const int MaxBatch = 5000;

    private readonly OsduDbContext _db;
    private readonly DeliveryDocumentLoader _documents;
    private readonly TimeProvider _time;
    private readonly Dictionary<(Guid Repo, string Reference), (MappingDefinition? Mapping, string? Problem)> _mappings = [];
    private readonly Dictionary<(Guid Repo, string? Document, TemplateReference Reference), TemplateResolution> _schemas = [];
    private readonly Dictionary<Guid, Catalog.SyncedTemplates> _folders = [];

    public SearchTermDirectory(OsduDbContext db, DeliveryDocumentLoader documents, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _documents = documents;
        _time = time;
    }

    /// <summary>The entity types terms are extracted for, each with how many terms it has, by name.</summary>
    public async Task<IReadOnlyList<(string EntityType, int Terms)>> EntityTypesAsync(CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking()
            .Select(t => new { t.EntityType, t.TermId })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .GroupBy(r => r.EntityType, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The terms of <paramref name="entityType"/> (every entity type when null, a term once per type it reaches), by name, each
    /// searched through the route it would be for <paramref name="kind"/> when one kind is named; with
    /// <paramref name="orphans"/>, the refinements whose terms no pipeline gives any longer too.
    /// </summary>
    public async Task<IReadOnlyList<SearchTermView>> ListAsync(string? entityType, string? kind, bool orphans, CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking()
            .Where(t => entityType == null || t.EntityType == entityType)
            .ToListAsync(ct).ConfigureAwait(false);
        var termIds = rows.Select(r => r.TermId).Distinct().ToList();
        var refinements = await _db.DeliverySearchTermRefinements.AsNoTracking()
            .Where(r => termIds.Contains(r.TermId) || (orphans && (entityType == null || r.EntityType == entityType)))
            .ToDictionaryAsync(r => r.TermId, ct).ConfigureAwait(false);

        var views = new List<SearchTermView>();
        foreach (var term in rows.GroupBy(r => (r.TermId, r.EntityType)))
        {
            views.Add(await ViewAsync(Merge(term.ToList()), refinements.GetValueOrDefault(term.Key.TermId), kind, ct).ConfigureAwait(false));
        }

        if (orphans)
        {
            var held = termIds.ToHashSet();
            var anywhere = await _db.DeliverySearchTerms.AsNoTracking()
                .Where(t => refinements.Keys.Contains(t.TermId))
                .Select(t => t.TermId)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            held.UnionWith(anywhere);
            views.AddRange(refinements.Values.Where(r => !held.Contains(r.TermId)).Select(Orphaned));
        }

        return views
            .OrderBy(v => v.EntityType, StringComparer.Ordinal)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The term <paramref name="termId"/> as it reaches <paramref name="entityType"/> (the first type it reaches, by name, when
    /// null or one it does not reach), searched through the route it would be for <paramref name="kind"/>; its refinement while
    /// no pipeline gives it; null for neither.
    /// </summary>
    public async Task<SearchTermView?> FindAsync(Guid termId, string? entityType, string? kind, CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking().Where(t => t.TermId == termId).ToListAsync(ct).ConfigureAwait(false);
        var refinement = await _db.DeliverySearchTermRefinements.AsNoTracking().FirstOrDefaultAsync(r => r.TermId == termId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return refinement is null ? null : Orphaned(refinement);
        }

        var type = rows.Any(r => string.Equals(r.EntityType, entityType, StringComparison.Ordinal))
            ? entityType!
            : rows.Select(r => r.EntityType).Order(StringComparer.Ordinal).First();
        return await ViewAsync(Merge(rows.Where(r => r.EntityType == type).ToList()), refinement, kind, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps what <paramref name="actor"/> made of the term: its name (unique among the terms of the entity type it is refined
    /// in), whether it is deleted, the route it is searched through (one of its routes that can be searched) and a note. A
    /// request that keeps nothing of the term's own removes its refinement.
    /// </summary>
    /// <exception cref="DeliveryException">The term does not exist, or the request is not one a term takes; the message says why.</exception>
    public async Task<SearchTermView> RefineAsync(Guid termId, string? entityType, SearchTermRefinementRequest request, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var current = await FindAsync(termId, entityType, null, ct).ConfigureAwait(false);
        if (current is null || current.Orphan)
        {
            throw new DeliveryException(current is null
                ? $"No search term {termId} is extracted from the pipelines of an active delivery flow."
                : $"The search term {current.Key} is no longer extracted from any pipeline, so only its refinement can be removed.");
        }

        var name = Clean(request.Name, DeliverySearchTermRefinement.MaxNameLength, "name", newlines: false);
        var note = Clean(request.Note, DeliverySearchTermRefinement.MaxNoteLength, "note", newlines: true);
        if (name is not null && string.Equals(name, current.ColumnLabel, StringComparison.Ordinal))
        {
            name = null;
        }

        if (name is not null)
        {
            var others = await ListAsync(current.EntityType, null, orphans: false, ct).ConfigureAwait(false);
            if (others.FirstOrDefault(o => o.Id != termId && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
            {
                throw new DeliveryException($"The name '{name}' is the search term {taken.ColumnLabel}'s already; two terms of {current.EntityType} cannot share one.");
            }
        }

        var route = string.IsNullOrWhiteSpace(request.Route) ? null : request.Route.Trim();
        if (route is not null)
        {
            var picked = current.Routes.FirstOrDefault(r => string.Equals(r.Id, route, StringComparison.Ordinal))
                ?? throw new DeliveryException($"'{route}' is not a route of the search term {current.ColumnLabel} on {current.EntityType}: {string.Join(", ", current.Routes.Select(r => r.Id))}.");
            if (picked.Problem is { } problem)
            {
                throw new DeliveryException($"The search term {current.ColumnLabel} cannot be searched through {picked.Target}: {problem}.");
            }
        }

        var row = await _db.DeliverySearchTermRefinements.AsTracking().FirstOrDefaultAsync(r => r.TermId == termId, ct).ConfigureAwait(false);
        if (KeepsNothing(name, request.Excluded, route, note))
        {
            if (row is not null)
            {
                _db.DeliverySearchTermRefinements.Remove(row);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return (await FindAsync(termId, current.EntityType, null, ct).ConfigureAwait(false))!;
        }

        if (row is null)
        {
            row = new DeliverySearchTermRefinement { TermId = termId };
            _db.DeliverySearchTermRefinements.Add(row);
        }

        row.TermKey = current.Key;
        row.EntityType = current.EntityType;
        row.Name = name;
        row.Excluded = request.Excluded;
        row.Route = route;
        row.Note = note;
        Stamp(row, actor, _time.GetUtcNow().UtcDateTime);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await FindAsync(termId, current.EntityType, null, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Deletes the terms <paramref name="termIds"/> as <paramref name="actor"/>, in one save. A term a pipeline reads cannot go,
    /// since the next sync extracts it again, so it is taken out of the search: its refinement marks it deleted and keeps its
    /// name, note and route, so a restore gives it back as it was (one made new is kept for <paramref name="entityType"/> when
    /// the term reaches it, as a refinement made on the Search terms page is). A term no pipeline reads any longer is only its
    /// refinement, which is removed for good. An id that is neither is reported missing.
    /// </summary>
    /// <exception cref="DeliveryException">No term is named, or more than <see cref="MaxBatch"/>.</exception>
    public async Task<SearchTermDeletion> DeleteAsync(IReadOnlyCollection<Guid> termIds, string? entityType, string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ids = Batch(termIds);
        var extracted = await ExtractedAsync(ids, ct).ConfigureAwait(false);
        var rows = await _db.DeliverySearchTermRefinements.AsTracking().Where(r => ids.Contains(r.TermId)).ToDictionaryAsync(r => r.TermId, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        List<Guid> deleted = [], removed = [], missing = [];
        foreach (var id in ids)
        {
            var row = rows.GetValueOrDefault(id);
            if (extracted.TryGetValue(id, out var term))
            {
                if (row is null)
                {
                    row = new DeliverySearchTermRefinement
                    {
                        TermId = id,
                        TermKey = term.Key,
                        EntityType = entityType is not null && term.EntityTypes.Contains(entityType, StringComparer.Ordinal) ? entityType : term.EntityTypes[0],
                    };
                    _db.DeliverySearchTermRefinements.Add(row);
                }

                // Deleted already, it keeps who deleted it and when.
                if (!row.Excluded)
                {
                    row.Excluded = true;
                    Stamp(row, actor, now);
                }

                deleted.Add(id);
            }
            else if (row is not null)
            {
                _db.DeliverySearchTermRefinements.Remove(row);
                removed.Add(id);
            }
            else
            {
                missing.Add(id);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new SearchTermDeletion(deleted, removed, missing);
    }

    /// <summary>
    /// Restores the deleted terms of <paramref name="termIds"/> as <paramref name="actor"/>, in one save: each is offered in the
    /// explorer again with the name, note and route it had, and a refinement that keeps nothing else is removed, as a refine
    /// keeping nothing removes it. A term offered already is restored as it is; an id no pipeline reads is reported missing,
    /// since only a term a pipeline reads can be searched.
    /// </summary>
    /// <exception cref="DeliveryException">No term is named, or more than <see cref="MaxBatch"/>.</exception>
    public async Task<SearchTermRestoration> RestoreAsync(IReadOnlyCollection<Guid> termIds, string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ids = Batch(termIds);
        var extracted = await ExtractedAsync(ids, ct).ConfigureAwait(false);
        var rows = await _db.DeliverySearchTermRefinements.AsTracking().Where(r => ids.Contains(r.TermId) && r.Excluded).ToDictionaryAsync(r => r.TermId, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        List<Guid> restored = [], missing = [];
        foreach (var id in ids)
        {
            if (!extracted.ContainsKey(id))
            {
                missing.Add(id);
                continue;
            }

            if (rows.GetValueOrDefault(id) is { } row)
            {
                row.Excluded = false;
                if (KeepsNothing(row.Name, row.Excluded, row.Route, row.Note))
                {
                    _db.DeliverySearchTermRefinements.Remove(row);
                }
                else
                {
                    Stamp(row, actor, now);
                }
            }

            restored.Add(id);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new SearchTermRestoration(restored, missing);
    }

    /// <summary>Removes what people made of the term, so it is searched as its pipelines give it; false when there was nothing to remove.</summary>
    public async Task<bool> ResetAsync(Guid termId, CancellationToken ct)
    {
        var removed = await _db.DeliverySearchTermRefinements.Where(r => r.TermId == termId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return removed > 0;
    }

    /// <summary>A refinement keeping none of these keeps nothing of the term's own, and is removed rather than kept.</summary>
    private static bool KeepsNothing(string? name, bool excluded, string? route, string? note)
        => name is null && !excluded && route is null && note is null;

    /// <summary>Records who changed a refinement and when, the name cut to what the table keeps.</summary>
    private static void Stamp(DeliverySearchTermRefinement row, string actor, DateTime nowUtc)
    {
        row.UpdatedBy = actor.Length <= 200 ? actor : actor[..200];
        row.UpdatedUtc = nowUtc;
    }

    /// <summary>The ids a deletion or restoration names, each once.</summary>
    /// <exception cref="DeliveryException">None is named, or more than <see cref="MaxBatch"/>.</exception>
    private static List<Guid> Batch(IReadOnlyCollection<Guid> termIds)
    {
        ArgumentNullException.ThrowIfNull(termIds);
        var ids = termIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new DeliveryException("Name the search terms to change: no term id was given.");
        }

        return ids.Count <= MaxBatch
            ? ids
            : throw new DeliveryException($"{ids.Count} search terms were named; at most {MaxBatch} are changed at once.");
    }

    /// <summary>The terms of <paramref name="ids"/> a pipeline reads, each with its key and the entity types it reaches, by name.</summary>
    private async Task<Dictionary<Guid, (string Key, IReadOnlyList<string> EntityTypes)>> ExtractedAsync(List<Guid> ids, CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking()
            .Where(t => ids.Contains(t.TermId))
            .Select(t => new { t.TermId, t.TermKey, t.EntityType })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .GroupBy(r => r.TermId)
            .ToDictionary(
                g => g.Key,
                g => (g.First().TermKey, (IReadOnlyList<string>)g.Select(r => r.EntityType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()));
    }

    /// <summary>
    /// The condition the explorer asks of the records of <paramref name="kind"/> for <paramref name="asked"/> on the term
    /// <paramref name="termId"/>, in <paramref name="partition"/>: on the property its route for that kind fills (the route
    /// of the mapping version that renders the kind, where versions write it differently), the values put through that
    /// mapping, and for a route through other records, the way the node reads those. A kind of many types takes the term's
    /// one type, and is refused for a term that reaches several.
    /// </summary>
    /// <exception cref="DeliveryException">The term does not exist, is deleted, cannot be searched, or a value cannot be carried.</exception>
    public async Task<ExplorerFilter> ResolveAsync(Guid termId, SearchTermCondition asked, string partition, string? kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var rows = await _db.DeliverySearchTerms.AsNoTracking().Where(t => t.TermId == termId).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new DeliveryException($"No search term {termId} is extracted from the pipelines of an active delivery flow; sync the repository, or pick the property itself.");
        }

        var types = rows.Select(r => r.EntityType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var label = new SearchTermKey(rows[0].Source, rows[0].Column).ColumnLabel;
        var named = string.IsNullOrWhiteSpace(kind) ? null : ExplorerKinds.EntityTypeOf(kind.Trim());
        string type;
        if (named is not null)
        {
            type = types.FirstOrDefault(t => string.Equals(t, named, StringComparison.Ordinal))
                ?? throw new DeliveryException($"{label} is a column of {string.Join(" and ", types)} records, not of {named}'s.");
        }
        else
        {
            type = types.Count == 1
                ? types[0]
                : throw new DeliveryException($"{label} reaches the records of {string.Join(" and ", types)}: pick one of those types to search it.");
        }

        var refinement = await _db.DeliverySearchTermRefinements.AsNoTracking().FirstOrDefaultAsync(r => r.TermId == termId, ct).ConfigureAwait(false);
        var term = Merge(rows.Where(r => r.EntityType == type).ToList());
        var view = await ViewAsync(term, refinement, kind, ct).ConfigureAwait(false);
        if (view.Excluded)
        {
            throw new DeliveryException($"{view.Name} is deleted from the search terms; restore it on the Search terms page.");
        }

        var chosenAt = view.Route is { } routeId ? term.Routes.ToList().FindIndex(r => r.Route.Id == routeId) : -1;
        if (chosenAt < 0)
        {
            throw new DeliveryException($"{view.Name} cannot be searched: {view.Problem}.");
        }

        var chosen = term.Routes[chosenAt];

        var (mapping, fields) = await ClassifyAsync(chosen.Route, chosen.Repo, MappingFor(chosen.Route, kind), ct).ConfigureAwait(false);
        var template = mapping is null ? null : (await TemplateAsync(chosen.Repo, mapping.TemplateDocument, mapping.Template, ct).ConfigureAwait(false)).Schema;
        if (mapping is null || template is null)
        {
            throw new DeliveryException($"{view.Name} cannot be searched: {fields.Problem}.");
        }

        SearchTermValues values;
        try
        {
            values = SearchTermValues.For(mapping, template, partition);
        }
        catch (FlowValidationException ex)
        {
            throw new DeliveryException($"{view.Name} cannot be searched in partition {partition}: {ex.Message}", ex);
        }

        return SearchTermResolver.Resolve(view.Name, term.Key, chosen.Route, fields, asked, values);
    }

    /// <summary>
    /// A term's rows of one entity type from every repository that gives it: its key, every route once (the latest sync's),
    /// and every flow.
    /// </summary>
    private static Term Merge(IReadOnlyList<DeliverySearchTerm> rows)
    {
        var first = rows[0];
        var key = new SearchTermKey(first.Source, first.Column);
        var routes = new Dictionary<string, (SearchRoute Route, Guid Repo, DateTime Seen)>(StringComparer.Ordinal);
        var flows = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var route in SearchRoute.FromJson(row.RoutesJson))
            {
                if (!routes.TryGetValue(route.Id, out var held) || held.Seen < row.LastSeenUtc)
                {
                    routes[route.Id] = (route, row.RepoId, row.LastSeenUtc);
                }
            }

            foreach (var flow in Flows(row.FlowsJson))
            {
                flows.Add(flow);
            }
        }

        return new Term(first.TermId, key, first.EntityType, routes.Values.Select(r => (r.Route, r.Repo)).ToList(), flows.ToList());
    }

    /// <summary>
    /// The mapping a route is classified and its values carried by: for a kind in view that one of the route's mappings
    /// renders, that mapping; else the newest that reads the route. Null for the newest.
    /// </summary>
    private static string? MappingFor(SearchRoute route, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Contains('*', StringComparison.Ordinal) || route.Kinds.Count != route.Mappings.Count)
        {
            return null;
        }

        var at = route.Kinds.ToList().FindIndex(k => string.Equals(k, kind.Trim(), StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null : route.Mappings[at];
    }

    private async Task<SearchTermView> ViewAsync(Term term, DeliverySearchTermRefinement? refinement, string? kind, CancellationToken ct)
    {
        var routes = new List<(SearchRoute Route, SearchRouteFields Fields)>(term.Routes.Count);
        foreach (var (route, repo) in term.Routes)
        {
            var (_, fields) = await ClassifyAsync(route, repo, MappingFor(route, kind), ct).ConfigureAwait(false);
            routes.Add((route, fields));
        }

        var usable = routes.Where(r => r.Fields.Target is not null).Select(r => r.Route).ToHashSet();
        var chosen = SearchTermResolver.Preferred(routes.Select(r => r.Route).ToList(), usable.Contains, refinement?.Route, kind);
        var views = routes
            .Select(r => View(r.Route, r.Fields))
            .OrderBy(v => chosen is not null && v.Id == chosen.Id ? 0 : 1)
            .ThenBy(v => v.Problem is null ? 0 : 1)
            .ThenBy(v => v.Target, StringComparer.Ordinal)
            .ToList();
        var problem = chosen is null
            ? (routes.FirstOrDefault().Fields?.Problem is { } first ? $"none of its routes can be searched; {first}" : "no route reaches the record")
            : null;
        return new SearchTermView
        {
            Id = term.Id,
            Key = term.Key.Text,
            Source = term.Key.Source,
            Table = term.Key.Table,
            EntityType = term.EntityType,
            Column = term.Key.Column,
            ColumnLabel = term.Key.ColumnLabel,
            Systems = term.Routes.SelectMany(r => r.Route.Systems).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Name = refinement?.Name ?? term.Key.ColumnLabel,
            Renamed = refinement?.Name is not null,
            Excluded = refinement?.Excluded ?? false,
            Note = refinement?.Note,
            PickedRoute = refinement?.Route,
            Route = chosen?.Id,
            Routes = views,
            Flows = term.Flows,
            Mappings = term.Routes.SelectMany(r => r.Route.Mappings).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Problem = problem,
            Suggest = chosen is null ? null : Suggest(chosen, routes.First(r => r.Route.Id == chosen.Id).Fields),
            UpdatedBy = refinement?.UpdatedBy,
            UpdatedUtc = refinement?.UpdatedUtc,
        };
    }

    /// <summary>
    /// A refinement whose term no pipeline gives any longer: its name and note, and no route. Its key names the table and the
    /// column (<c>osdudata.arc.welllog/wellbore_uwi</c>); one made while terms were keyed by their mapping's source system
    /// names those instead.
    /// </summary>
    private static SearchTermView Orphaned(DeliverySearchTermRefinement refinement)
    {
        string source, column, label;
        IReadOnlyList<string> systems;
        if (SearchTermKey.Legacy(refinement.TermKey) is { } legacy)
        {
            (source, column, systems) = (string.Empty, legacy.Column, [legacy.System]);
            label = legacy.Dataset.Length == 0 ? legacy.Column : $"{legacy.Dataset}.{legacy.Column}";
        }
        else
        {
            (source, column) = SplitKey(refinement.TermKey);
            systems = [];
            label = source.Length == 0 ? column : $"{SearchTermKey.TableOf(source)}.{column}";
        }

        return new SearchTermView
        {
            Id = refinement.TermId,
            Key = refinement.TermKey,
            Source = source,
            Table = source.Length == 0 ? string.Empty : SearchTermKey.TableOf(source),
            EntityType = refinement.EntityType,
            Column = column,
            ColumnLabel = label,
            Systems = systems,
            Name = refinement.Name ?? label,
            Renamed = refinement.Name is not null,
            Excluded = refinement.Excluded,
            Note = refinement.Note,
            PickedRoute = refinement.Route,
            Problem = "no pipeline of an active delivery flow reads this column any longer",
            Orphan = true,
            UpdatedBy = refinement.UpdatedBy,
            UpdatedUtc = refinement.UpdatedUtc,
        };
    }

    /// <summary>The table and the column a key names (<c>osdudata.arc.welllog/wellbore_uwi</c>).</summary>
    private static (string Source, string Column) SplitKey(string termKey)
    {
        var slash = termKey.LastIndexOf('/');
        return slash <= 0 ? (string.Empty, termKey) : (termKey[..slash], termKey[(slash + 1)..]);
    }

    /// <summary>
    /// Where the values to pick from are read for a term searched through <paramref name="route"/>: its property, for a route
    /// whose written values are the ones typed (a copy, steps that keep the text); the property its records are found by, for a
    /// lookup or a search; none for any other.
    /// </summary>
    private static SearchTermSuggest? Suggest(SearchRoute route, SearchRouteFields fields)
    {
        switch (route.Kind)
        {
            case SearchRouteKind.Copy:
            case SearchRouteKind.Steps when route.Keeps == SearchRouteKeeps.Text:
                return fields.Target is { } target ? new SearchTermSuggest(null, target.Path, IndexName(target.Index), target.NestedPath) : null;
            case SearchRouteKind.Lookup or SearchRouteKind.Search when fields.Match is { Count: > 0 } match:
                return new SearchTermSuggest(route.Find!.Kind, match[0].Path, IndexName(match[0].Index), match[0].NestedPath);
            default:
                return null;
        }
    }

    private static SearchRouteView View(SearchRoute route, SearchRouteFields fields) => new()
    {
        Id = route.Id,
        EntityType = route.EntityType,
        Kinds = route.Kinds,
        Dataset = route.Dataset,
        Target = route.Target,
        Path = route.Path,
        Kind = route.Kind.ToString().ToLowerInvariant(),
        How = SearchTermResolver.Spoken(route.Kind),
        Steps = route.Steps,
        Mappings = route.Mappings,
        Location = route.Location,
        Find = route.Find,
        KeyColumns = route.Key?.Columns,
        When = route.When,
        Description = route.Description,
        Index = fields.Target is { } target ? IndexName(target.Index) : null,
        Nested = fields.Target?.NestedPath,
        Conditions = SearchTermResolver.Conditions(route, fields).Select(ConditionName).ToList(),
        Problem = fields.Problem,
    };

    /// <summary>
    /// How the route compares, as the schemas say it is indexed, with the mapping it reads (<paramref name="reference"/>, or
    /// the newest that reads it); or why it cannot be searched.
    /// </summary>
    private async Task<(MappingDefinition? Mapping, SearchRouteFields Fields)> ClassifyAsync(SearchRoute route, Guid repo, string? reference, CancellationToken ct)
    {
        if (route.Problem is { } problem)
        {
            return (null, SearchRouteFields.Refused(problem));
        }

        reference ??= route.Mappings[^1];
        var (mapping, mappingProblem) = await MappingAsync(repo, reference, ct).ConfigureAwait(false);
        if (mapping is null)
        {
            return (null, SearchRouteFields.Refused(mappingProblem!));
        }

        var resolved = await TemplateAsync(repo, mapping.TemplateDocument, mapping.Template, ct).ConfigureAwait(false);
        if (resolved.Schema is not { } template)
        {
            return (mapping, SearchRouteFields.Refused($"the template of {reference} cannot be read: {resolved.Problem}"));
        }

        SchemaSnapshot? found = null;
        if (route.Find is { } find)
        {
            if (route.Kind == SearchRouteKind.Search && mapping.Searches.TryGetValue(find.Name, out var search) && search.Schema is { } pinned)
            {
                found = (await TemplateAsync(repo, mapping.TemplateDocument, pinned, ct).ConfigureAwait(false)).Schema;
            }
            else
            {
                var (matching, _) = await DimensionBlueprints.MatchingAsync(find.Kind, await FoldersAsync(repo, ct).ConfigureAwait(false), mapping.TemplateDocument, ct).ConfigureAwait(false);
                found = matching.Count == 0 ? null : matching[0].Schema;
            }
        }

        return (mapping, SearchTermResolver.Classify(route, template, found));
    }

    private async Task<(MappingDefinition? Mapping, string? Problem)> MappingAsync(Guid repo, string reference, CancellationToken ct)
    {
        if (_mappings.TryGetValue((repo, reference), out var known))
        {
            return known;
        }

        var row = await _db.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repo && m.Reference == reference && m.Status == "valid")
            .Select(m => new { m.Yaml, m.RelativePath })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        (MappingDefinition?, string?) found;
        if (row is null)
        {
            found = (null, $"its mapping {reference} is no longer in the catalog; sync the repository again");
        }
        else
        {
            try
            {
                var folders = await FoldersAsync(repo, ct).ConfigureAwait(false);
                found = (_documents.ParseMapping(row.Yaml, row.RelativePath, folders.For(row.RelativePath)), null);
            }
            catch (FlowValidationException ex)
            {
                found = (null, $"its mapping {reference} no longer reads: {ex.Message}");
            }
        }

        _mappings[(repo, reference)] = found;
        return found;
    }

    /// <summary>
    /// The template <paramref name="reference"/> as the document at <paramref name="document"/> of repository
    /// <paramref name="repo"/> reads it, from the template folders the repository's last sync recorded on its path.
    /// </summary>
    private async Task<TemplateResolution> TemplateAsync(Guid repo, string? document, TemplateReference reference, CancellationToken ct)
    {
        var key = (repo, document, reference);
        if (!_schemas.TryGetValue(key, out var resolved))
        {
            resolved = await (await FoldersAsync(repo, ct).ConfigureAwait(false)).ResolveAsync(document, reference.Kind, reference.Version, ct).ConfigureAwait(false);
            _schemas[key] = resolved;
        }

        return resolved;
    }

    /// <summary>The template folders the last sync of repository <paramref name="repo"/> recorded, read once.</summary>
    private async Task<Catalog.SyncedTemplates> FoldersAsync(Guid repo, CancellationToken ct)
    {
        if (!_folders.TryGetValue(repo, out var folders))
        {
            folders = await Catalog.SyncedTemplates.ReadAsync(_db, repo, ct).ConfigureAwait(false);
            _folders[repo] = folders;
        }

        return folders;
    }

    private static IReadOnlyList<string> Flows(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Text a person gave for a term, trimmed and checked; null for none.</summary>
    private static string? Clean(string? text, int max, string what, bool newlines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > max)
        {
            throw new DeliveryException($"A term's {what} is at most {max} characters; this one is {trimmed.Length}.");
        }

        if (trimmed.Any(c => char.IsControl(c) && !(newlines && c is '\n' or '\r' or '\t')))
        {
            throw new DeliveryException($"A term's {what} holds a character that cannot be printed.");
        }

        return trimmed;
    }

    /// <summary>How the platform indexes a property, as the explorer names it.</summary>
    public static string IndexName(OsduFieldIndex index) => index.ToString().ToLowerInvariant();

    /// <summary>A condition as the explorer names it: <c>isNot</c>, <c>startsWith</c>.</summary>
    public static string ConditionName(ExplorerCondition condition) => JsonNamingPolicy.CamelCase.ConvertName(condition.ToString());

    /// <summary>
    /// A term as its rows of one entity type give it: its identity and key, the entity type, every route with the repository
    /// its mapping is read from, and the flows that read it.
    /// </summary>
    private sealed record Term(Guid Id, SearchTermKey Key, string EntityType, IReadOnlyList<(SearchRoute Route, Guid Repo)> Routes, IReadOnlyList<string> Flows);
}
