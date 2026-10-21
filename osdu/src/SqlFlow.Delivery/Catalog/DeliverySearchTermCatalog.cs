using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.SearchTerms;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The search terms of a repository (osdu/docs/reference/concepts/search-terms.md), extracted from its pipelines: every
/// active delivery flow (each interface of a source) reads its tables and renders them with a mapping, and every column
/// of those tables the mapping reads is a term, with every route by which it reaches the records rendered
/// (<see cref="SearchTermCompiler"/>) and the flows that read it. A term is the table's column: two flows reading one
/// table, rendering it under two versions of a schema, give one term. The repository sync writes them after the
/// mappings, the cache declarations and the interfaces, in the same transaction, from the flows it parsed; the control
/// plane writes them again when it starts, from its copies of the flows, so a module upgraded since the last sync
/// describes its terms as this version does. What a person made of a term is kept apart, by the term's identity, and
/// never undone here.
/// </summary>
public static class DeliverySearchTermCatalog
{
    /// <summary>
    /// Writes the search terms of <paramref name="repoId"/> into <paramref name="context"/> from <paramref name="sources"/>,
    /// the repository's delivery flows as parsed (the active ones are read), the valid mappings their interfaces pin, and the
    /// cached types the cache flows declare. A term no pipeline gives any longer is removed; the counts say what changed. What
    /// a person made of a term while terms were keyed by their mapping's source system is moved to the term it is now.
    /// </summary>
    public static async Task<(int Added, int Updated, int Unchanged, int Removed)> ReconcileAsync(
        OsduDbContext context, Guid repoId, IReadOnlyList<RepositorySource> sources, DeliveryDocumentLoader documents, DateTime nowUtc,
        ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(warnings);

        // Every interface of every active delivery flow: the mapping it renders with and the tables it reads.
        var readings = sources
            .Where(s => s.Active)
            .SelectMany(s => s.Source.Interfaces.Select(flow => new Reading(
                s.Source.Name,
                flow.Render.Mapping,
                SearchTermKey.NormalizeSource(flow.Source.Record.Object),
                flow.Source.Datasets.ToDictionary(d => d.Key, d => SearchTermKey.NormalizeSource(d.Value.Object), StringComparer.Ordinal))))
            .ToList();
        var pinned = readings.Select(r => r.Mapping).Distinct(StringComparer.Ordinal).ToList();
        var rows = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId && m.Status == "valid" && pinned.Contains(m.Reference))
            .Select(m => new { m.Reference, m.Yaml, m.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        // The template folders this sync has just recorded, which settle a mapping that names its template's kind alone.
        var folders = await SyncedTemplates.ReadAsync(context, repoId, ct).ConfigureAwait(false);
        var mappings = new Dictionary<string, MappingDefinition>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(r => r.Reference, StringComparer.Ordinal))
        {
            try
            {
                mappings[row.Reference] = documents.ParseMapping(row.Yaml, row.RelativePath, folders.For(row.RelativePath));
            }
            catch (FlowValidationException ex)
            {
                // A mapping row is valid as the sync found it; one that no longer parses (a module upgraded since) gives no terms.
                warnings.Add($"{row.RelativePath}: its search terms are left out, since the mapping no longer reads: {ex.Message}");
            }
        }

        // One reading per mapping and tables: the partitions and interfaces that repeat it read nothing more.
        var distinct = readings
            .Where(r => mappings.ContainsKey(r.Mapping))
            .GroupBy(r => (r.Mapping, r.RecordObject, Datasets: string.Join("|", r.Datasets.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}={d.Value}"))))
            .Select(g => new SearchTermSource(mappings[g.Key.Mapping], g.Key.RecordObject, g.First().Datasets))
            .ToList();
        var cacheTypes = await CacheTypesAsync(context, repoId, warnings, ct).ConfigureAwait(false);
        var compiled = SearchTermCompiler.Compile(distinct, name => cacheTypes.GetValueOrDefault(name), warnings);

        var existing = await context.DeliverySearchTerms
            .Where(t => t.RepoId == repoId)
            .AsTracking()
            .ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, unchanged = 0, removed = 0;
        var kept = new List<CompiledSearchTerm>(compiled.Count);
        foreach (var term in compiled)
        {
            var key = term.Key;
            if (Unfit(key, term.EntityType) is { } why)
            {
                warnings.Add($"The search term {key.Text} of {term.EntityType} is left out: {why}.");
                continue;
            }

            kept.Add(term);
            var id = FlowIdentity.FromName($"delivery-search-term/{repoId:N}/{term.EntityType.ToLowerInvariant()}/{key.Text}");
            seen.Add(id);
            // The flows that read the term's table with a mapping its routes are read by.
            var mappingsOf = term.Routes.SelectMany(r => r.Mappings).ToHashSet(StringComparer.Ordinal);
            var flows = readings
                .Where(r => mappingsOf.Contains(r.Mapping) && r.Reads(key.Source))
                .Select(r => r.Flow)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var routesJson = SearchRoute.ToJson(term.Routes);
            var flowsJson = JsonSerializer.Serialize(flows);
            if (!existing.TryGetValue(id, out var row))
            {
                context.DeliverySearchTerms.Add(new DeliverySearchTerm
                {
                    Id = id,
                    RepoId = repoId,
                    TermId = key.Id,
                    TermKey = key.Text,
                    Source = key.Source,
                    EntityType = term.EntityType,
                    Column = key.Column,
                    RoutesJson = routesJson,
                    FlowsJson = flowsJson,
                    FirstSeenUtc = nowUtc,
                    LastSeenUtc = nowUtc,
                });
                added++;
                continue;
            }

            row.LastSeenUtc = nowUtc;
            if (row.RoutesJson == routesJson && row.FlowsJson == flowsJson && row.Column == key.Column && row.Source == key.Source)
            {
                unchanged++;
                continue;
            }

            row.RoutesJson = routesJson;
            row.FlowsJson = flowsJson;
            row.Column = key.Column;
            row.Source = key.Source;
            updated++;
        }

        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                context.DeliverySearchTerms.Remove(row);
                removed++;
            }
        }

        await AdoptLegacyRefinementsAsync(context, kept, ct).ConfigureAwait(false);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return (added, updated, unchanged, removed);
    }

    /// <summary>One interface's reading: its flow, the mapping it renders with, and its tables (the record table, and each dataset's by name).</summary>
    private sealed record Reading(string Flow, string Mapping, string RecordObject, IReadOnlyDictionary<string, string> Datasets)
    {
        /// <summary>Whether the interface reads <paramref name="table"/>, as its record table or a dataset's.</summary>
        public bool Reads(string table)
            => string.Equals(RecordObject, table, StringComparison.OrdinalIgnoreCase)
               || Datasets.Values.Any(d => string.Equals(d, table, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Moves what people made of terms while a term was keyed by its mapping's source system, its entity type and its dataset
    /// (<c>welldb/work-product-component--WellLog/curves/curve_unit</c>) to the term that column now is: the one of
    /// <paramref name="terms"/> in that entity type, read under that system and dataset. The newest of several made for one
    /// term is moved; the others, like any no term matches, stay as they are and are listed as no longer found, to be removed.
    /// A route picked is moved with it where the term still has it.
    /// </summary>
    private static async Task AdoptLegacyRefinementsAsync(OsduDbContext context, IReadOnlyList<CompiledSearchTerm> terms, CancellationToken ct)
    {
        var refinements = await context.DeliverySearchTermRefinements.AsTracking().ToListAsync(ct).ConfigureAwait(false);
        var legacy = refinements.Where(r => SearchTermKey.Legacy(r.TermKey) is not null).OrderByDescending(r => r.UpdatedUtc).ToList();
        if (legacy.Count == 0)
        {
            return;
        }

        var refined = refinements.Select(r => r.TermId).ToHashSet();
        foreach (var old in legacy)
        {
            var (system, entityType, dataset, column) = SearchTermKey.Legacy(old.TermKey)!.Value;
            var matches = terms
                .Where(t => string.Equals(t.EntityType, entityType, StringComparison.Ordinal)
                    && string.Equals(t.Key.Column, column, StringComparison.OrdinalIgnoreCase)
                    && t.Routes.Any(r => string.Equals(r.Dataset ?? string.Empty, dataset, StringComparison.Ordinal) && r.Systems.Contains(system, StringComparer.Ordinal)))
                .ToList();
            if (matches.Select(t => t.Key.Id).Distinct().Count() != 1 || refined.Contains(matches[0].Key.Id))
            {
                continue;
            }

            var term = matches[0];
            var route = old.Route is { } picked ? term.Routes.FirstOrDefault(r => string.Equals(r.Id, $"{entityType}|{picked}", StringComparison.Ordinal))?.Id : null;
            context.DeliverySearchTermRefinements.Add(new DeliverySearchTermRefinement
            {
                TermId = term.Key.Id,
                TermKey = term.Key.Text,
                EntityType = entityType,
                Name = old.Name,
                Excluded = old.Excluded,
                Route = route,
                Note = old.Note,
                UpdatedBy = old.UpdatedBy,
                UpdatedUtc = old.UpdatedUtc,
            });
            context.DeliverySearchTermRefinements.Remove(old);
            refined.Add(term.Key.Id);
        }
    }

    /// <summary>
    /// The cached types the mappings' lookups read, by name, as the cache flows declare them: the repository's own first,
    /// then any other's, each named by the first flow that declares it. A declaration whose fields do not read is passed
    /// over with a warning.
    /// </summary>
    private static async Task<Dictionary<string, SearchCacheType>> CacheTypesAsync(
        OsduDbContext context, Guid repoId, ICollection<string> warnings, CancellationToken ct)
    {
        var declarations = await context.DeliveryCacheDefinitions.AsNoTracking()
            .Select(c => new { c.RepoId, c.FlowName, c.Name, c.Origin, c.Kind, c.FieldsJson, c.Scope })
            .ToListAsync(ct).ConfigureAwait(false);
        var types = new Dictionary<string, SearchCacheType>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in declarations
            .OrderBy(c => c.RepoId == repoId ? 0 : 1)
            .ThenBy(c => c.FlowName, StringComparer.Ordinal)
            .ThenBy(c => c.Scope, StringComparer.Ordinal))
        {
            if (types.ContainsKey(declaration.Name))
            {
                continue;
            }

            try
            {
                var fields = OsduCacheStore.ParseFields(declaration.FieldsJson, declaration.FlowName, declaration.Name);
                types[declaration.Name] = new SearchCacheType(declaration.Name, declaration.Origin, declaration.Kind, fields);
            }
            catch (DeliveryException ex)
            {
                warnings.Add($"The cached type {declaration.Name} of {declaration.FlowName} is not read for search terms: {ex.Message}");
            }
        }

        return types;
    }

    /// <summary>Why a term's key does not fit the table's columns, or null when it does.</summary>
    private static string? Unfit(SearchTermKey key, string entityType)
    {
        if (key.Text.Length > DeliveryModel.SearchTermKeyLength)
        {
            return $"its key is {key.Text.Length} characters, and at most {DeliveryModel.SearchTermKeyLength} are kept";
        }

        if (key.Source.Length > DeliverySearchTerm.MaxSourceLength)
        {
            return $"its table's name is longer than {DeliverySearchTerm.MaxSourceLength} characters";
        }

        if (entityType.Length > DeliverySearchTerm.MaxEntityTypeLength)
        {
            return $"its entity type is longer than {DeliverySearchTerm.MaxEntityTypeLength} characters";
        }

        return key.Column.Length > DeliverySearchTerm.MaxColumnLength ? $"its column is longer than {DeliverySearchTerm.MaxColumnLength} characters" : null;
    }
}
