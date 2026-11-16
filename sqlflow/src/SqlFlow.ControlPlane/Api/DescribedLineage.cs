using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One lineage description (flowType: lin) a lineage walk crossed, or a list names: the step another system
/// runs, as its document says it. <c>System</c>, <c>Unit</c> and <c>Description</c> are null when the stored document
/// no longer reads.</summary>
public sealed record DescribedStepSummaryDto(
    Guid PipelineId, Guid RepoId, string Name, string File, string? System, string? Unit, string? Description);

/// <summary>One object a described step is connected to. <c>DescribedObject</c> is true for an object only descriptions
/// name (it has no registry row, and <c>System</c> says where it lives); false for an object of the estate, which the
/// description reached by naming the flow that reads or writes it.</summary>
public sealed record DescribedConnectionDto(string ObjectKey, string ObjectName, bool DescribedObject, string? System);

/// <summary>
/// One described step with where it is connected: the objects it reads and writes as the last sync placed them, how
/// many of its columns are mapped, and whether its document carries context. The document itself (the context, the
/// evidence, the notes) is the pipeline's stored text. <c>Problem</c> says why the summary fields are missing when
/// the stored document no longer reads.
/// </summary>
public sealed record DescribedStepDto(
    Guid PipelineId, Guid RepoId, string RepoName, string Name, string File, string? System, string? Unit,
    string? Description, string? Owner, string? Url, bool HasContext, int EvidenceCount,
    IReadOnlyList<DescribedConnectionDto> Reads, IReadOnlyList<DescribedConnectionDto> Writes, int MappedColumns,
    string? Problem);

/// <summary>One object and column a mapped column passes through between its target and a source further up.</summary>
public sealed record ColumnHopDto(string Flow, string ObjectKey, string ObjectName, string Column, string? Note);

/// <summary>One source column of a mapped column. <c>Via</c> lists the objects and columns in between when the source
/// is more than one described step away, nearest the target first; empty for a direct source.</summary>
public sealed record ColumnSourceDto(string ObjectKey, string ObjectName, string Column, IReadOnlyList<ColumnHopDto> Via);

/// <summary>One mapped column of a described object: where its value comes from and how it is made, as the lineage
/// description <c>Flow</c> says. A <c>*</c> in a column name stands for every column not listed by name. No
/// <c>Sources</c> means the described step makes the column itself.</summary>
public sealed record ColumnMappingDto(
    Guid PipelineId, string Flow, string TargetObjectKey, string TargetObjectName, string TargetColumn, int Ordinal,
    IReadOnlyList<ColumnSourceDto> Sources, string? Note);

/// <summary>
/// Column mappings of the described lineage. <c>Composed</c> is true when the sources were followed through more than
/// one described step to reach <c>FromKey</c>; <c>Truncated</c> when a limit cut the answer, so a missing column or
/// source is then not proof of absence. <c>Notes</c> says in words what the answer does not hold and why (no
/// description maps the object's columns, the column asked for is not mapped). Column mappings are an optional level
/// of detail: most described steps declare none.
/// </summary>
public sealed record ColumnMappingsDto(
    string? ToKey, string? ToName, string? FromKey, string? FromName,
    IReadOnlyList<ColumnMappingDto> Columns, bool Composed, bool Truncated, IReadOnlyList<string> Notes);

/// <summary>
/// The described lineage as the API serves it: what lineage descriptions (flowType: lin) say of steps other systems
/// run. Everything here is read on request. No other endpoint returns described lineage unless it is asked to
/// (<c>described=true</c>), so the lineage SQLFlow computed reads the same with or without descriptions.
/// </summary>
internal static class DescribedLineage
{
    /// <summary>The kind a lineage walk reports for an object only descriptions name.</summary>
    public const string DescribedKind = "Described";

    private const string DescribedReads = nameof(LineageRelation.DescribedReads);
    private const string DescribedWrites = nameof(LineageRelation.DescribedWrites);

    /// <summary>How many described steps a column is followed through by default, and at most.</summary>
    private const int DefaultColumnHops = 8;
    private const int MaxColumnHops = 12;

    /// <summary>The most mapped columns one answer carries, and the most objects whose mappings one composition reads.</summary>
    private const int MaxMappedColumns = 2000;
    private const int MaxComposedObjects = 60;

    /// <summary>The most columns one composition follows in all, and the most sources it reports for one column. A
    /// column made from many, each made from many again, multiplies at every step; these end that with an answer
    /// that says it was cut, where the number of paths would otherwise decide how long a request runs.</summary>
    private const int MaxComposedVisits = 20000;
    private const int MaxComposedSources = 500;

    private const int InChunk = 1000;

    /// <summary>Whether any of <paramref name="keys"/> has described lineage in the walked direction: a description
    /// writing it (upstream) or reading it (downstream).</summary>
    public static async Task<bool> TouchesAsync(
        CatalogDbContext db, IReadOnlyList<string> keys, string direction, CancellationToken ct)
    {
        var upstream = direction is "BOTH" or "UPSTREAM";
        var downstream = direction is "BOTH" or "DOWNSTREAM";
        for (var i = 0; i < keys.Count; i += InChunk)
        {
            var slice = keys.Skip(i).Take(InChunk).ToList();
            if (await db.LineageEdges.AsNoTracking()
                    .AnyAsync(
                        e => slice.Contains(e.ObjectKey)
                            && ((upstream && e.Relation == DescribedWrites) || (downstream && e.Relation == DescribedReads)),
                        ct)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a walk that followed the described lineage adds to its steps: each description it crossed, and for every
    /// object only descriptions name, whether the described chain ends there (no description writes it) and whether a
    /// description marks that end as the origin.
    /// </summary>
    public static async Task<(List<LineageStepDto> Upstream, List<LineageStepDto> Downstream, List<DescribedStepSummaryDto> Steps)> AnnotateWalkAsync(
        CatalogDbContext db, List<LineageStepDto> upstream, List<LineageStepDto> downstream, CancellationToken ct)
    {
        var crossed = upstream.Concat(downstream)
            .Where(s => s.Described && Guid.TryParse(s.PipelineId, out _))
            .Select(s => Guid.Parse(s.PipelineId!))
            .Distinct()
            .ToList();
        var documents = await LoadDocumentsAsync(db, crossed, ct).ConfigureAwait(false);

        var describedKeys = upstream.Concat(downstream)
            .Where(s => s.System is not null)
            .Select(s => s.ObjectKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var written = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < describedKeys.Count; i += InChunk)
        {
            var slice = describedKeys.Skip(i).Take(InChunk).ToList();
            written.UnionWith(await db.LineageEdges.AsNoTracking()
                .Where(e => slice.Contains(e.ObjectKey) && e.Relation == DescribedWrites)
                .Select(e => e.ObjectKey)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false));
        }

        // An origin is marked by a description reading the object; any description of the walk marking it counts.
        var origins = documents.Values
            .Where(d => d.Flow is not null)
            .SelectMany(d => d.Flow!.Reads.Where(r => r.Origin && !r.NamesFlow)
                .Select(r => NodeKey.For(LineageDescribedObject.ServerRef(r.System ?? d.Flow!.System), null, null, r.Name!)))
            .ToHashSet(StringComparer.Ordinal);

        LineageStepDto Annotate(LineageStepDto step)
            => step.System is null || written.Contains(step.ObjectKey)
                ? step
                : step with { Boundary = origins.Contains(step.ObjectKey) ? "origin" : "open" };

        var summaries = crossed
            .Where(documents.ContainsKey)
            .Select(id => documents[id])
            .Select(d => new DescribedStepSummaryDto(
                d.Id, d.RepoId, d.Name, d.File, d.Flow?.System, d.Flow?.Unit, d.Flow?.Description))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ([.. upstream.Select(Annotate)], [.. downstream.Select(Annotate)], summaries);
    }

    /// <summary>
    /// The described steps the catalog holds, by name: each with the objects the last sync connected it to and how many
    /// columns it maps. <paramref name="search"/> matches a word of the name or of the document's text.
    /// </summary>
    public static async Task<Ok<PagedResult<DescribedStepDto>>> ListStepsAsync(
        CatalogDbContext db, Guid? repoId, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.Pipelines.AsNoTracking()
            .Where(x => x.Kind == LineageDescriptionFlow.FlowType && x.Active);
        if (repoId is { } repo)
        {
            query = query.Where(x => x.RepoId == repo);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Name.Contains(term) || x.Yaml.Contains(term));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((p - 1) * size).Take(size)
            .Join(db.Repos.AsNoTracking(), x => x.RepoId, r => r.Id,
                (x, r) => new { x.Id, x.RepoId, RepoName = r.Name, x.Name, x.RelativePath, x.Yaml })
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = rows.Select(r => r.Id).ToList();
        var edges = ids.Count == 0
            ? []
            : await db.LineageEdges.AsNoTracking()
                .Where(e => e.PipelineId != null && ids.Contains(e.PipelineId.Value)
                    && (e.Relation == DescribedReads || e.Relation == DescribedWrites))
                .OrderBy(e => e.ObjectName).ThenBy(e => e.Id)
                .Select(e => new { PipelineId = e.PipelineId!.Value, e.Relation, e.ObjectKey, e.ObjectName })
                .ToListAsync(ct).ConfigureAwait(false);
        var mapped = ids.Count == 0
            ? []
            : await db.LineageColumnMappings.AsNoTracking()
                .Where(m => ids.Contains(m.PipelineId))
                .Select(m => new { m.PipelineId, m.TargetObjectKey, m.TargetColumn })
                .Distinct()
                .GroupBy(m => m.PipelineId)
                .Select(g => new { PipelineId = g.Key, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false);
        var mappedByPipeline = mapped.ToDictionary(m => m.PipelineId, m => m.Count);

        List<DescribedConnectionDto> Connections(Guid pipelineId, string relation)
            => [.. edges
                .Where(e => e.PipelineId == pipelineId && e.Relation == relation)
                .Select(e => Connection(e.ObjectKey, e.ObjectName))];

        var items = rows
            .Select(r =>
            {
                var (flow, problem) = Read(r.Yaml, r.RelativePath);
                return new DescribedStepDto(
                    r.Id, r.RepoId, r.RepoName, r.Name, r.RelativePath, flow?.System, flow?.Unit, flow?.Description,
                    flow?.Owner, flow?.Url, !string.IsNullOrWhiteSpace(flow?.Context), flow?.Evidence.Count ?? 0,
                    Connections(r.Id, DescribedReads), Connections(r.Id, DescribedWrites),
                    mappedByPipeline.GetValueOrDefault(r.Id), problem);
            })
            .ToList();
        return TypedResults.Ok(new PagedResult<DescribedStepDto>(items, p, size, total));
    }

    /// <summary>
    /// Column mappings of the described lineage. With <paramref name="to"/> alone: what the columns of that object come
    /// from, one described step up. With <paramref name="from"/> as well: only what comes from that object, followed
    /// through as many described steps as lie between the two. With <paramref name="pipelineId"/> alone: everything
    /// one description maps. <paramref name="column"/> narrows the answer to one column of <paramref name="to"/>.
    /// </summary>
    public static async Task<Results<Ok<ColumnMappingsDto>, ProblemHttpResult>> GetColumnMappingsAsync(
        CatalogDbContext db, string? to, string? from, string? column, Guid? pipelineId, int? depth, CancellationToken ct)
    {
        to = string.IsNullOrWhiteSpace(to) ? null : to.Trim();
        from = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        column = string.IsNullOrWhiteSpace(column) ? null : column.Trim();
        if (to is null && pipelineId is null)
        {
            return TypedResults.Problem(
                detail: "Name the object whose columns are asked about with 'to' (an object key), or one lineage description with 'pipelineId'.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "No target");
        }

        if (to is null && (from is not null || column is not null))
        {
            return TypedResults.Problem(
                detail: "'from' and 'column' narrow the columns of one object, so they go with 'to'.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "No target");
        }

        var query = db.LineageColumnMappings.AsNoTracking().AsQueryable();
        if (to is not null)
        {
            query = query.Where(m => m.TargetObjectKey == to);
        }

        if (pipelineId is { } pid)
        {
            query = query.Where(m => m.PipelineId == pid);
        }

        var rows = await query
            .OrderBy(m => m.Flow).ThenBy(m => m.TargetObjectName).ThenBy(m => m.Ordinal).ThenBy(m => m.SourceOrdinal).ThenBy(m => m.Id)
            .Take(MaxMappedColumns + 1)
            .ToListAsync(ct).ConfigureAwait(false);
        var truncated = rows.Count > MaxMappedColumns;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var notes = new List<string>();
        var toName = rows.FirstOrDefault()?.TargetObjectName;
        if (rows.Count == 0)
        {
            notes.Add(to is not null
                ? "No lineage description maps columns of this object. Column mappings are an optional level of detail; the object-level lineage (the lineage walk with described=true) may still say where the object comes from."
                : "This lineage description maps no columns.");
            return TypedResults.Ok(new ColumnMappingsDto(to, null, from, null, [], Composed: false, truncated, notes));
        }

        var groups = Group(rows);
        var mapped = groups;
        if (column is not null)
        {
            // A column asked for by name is answered by its own mapping, or else by the entry standing for the rest.
            var named = groups.Where(g => !g.Key.Column.Contains('*', StringComparison.Ordinal)
                    && string.Equals(g.Key.Column, column, StringComparison.OrdinalIgnoreCase))
                .ToList();
            groups = named.Count > 0
                ? named
                : [.. groups.Where(g => ColumnGlob.Capture(g.Key.Column, column) is not null)];
            if (groups.Count == 0)
            {
                notes.Add($"Column '{column}' is not mapped. A column that is not listed is not mapped, which says nothing about whether it exists.");
                return TypedResults.Ok(new ColumnMappingsDto(to, toName, from, null, [], Composed: false, truncated, notes));
            }
        }

        if (from is null)
        {
            var direct = groups.Select(g => Direct(g, column)).ToList();
            return TypedResults.Ok(new ColumnMappingsDto(to, toName, null, null, direct, Composed: false, truncated, notes));
        }

        var hops = Math.Clamp(depth ?? DefaultColumnHops, 1, MaxColumnHops);
        var cut = false;
        var composer = new ColumnComposer(from, MaxComposedObjects, MaxComposedVisits, MaxComposedSources, async key =>
        {
            var stored = await db.LineageColumnMappings.AsNoTracking()
                .Where(m => m.TargetObjectKey == key)
                .OrderBy(m => m.Ordinal).ThenBy(m => m.SourceOrdinal).ThenBy(m => m.Id)
                .Take(MaxMappedColumns + 1)
                .ToListAsync(ct).ConfigureAwait(false);
            if (stored.Count > MaxMappedColumns)
            {
                // More than one answer reads of an object: what is followed from here is not everything.
                cut = true;
                stored.RemoveAt(stored.Count - 1);
            }

            return Group(stored);
        }, ct);
        var composed = new List<ColumnMappingDto>();
        foreach (var group in groups)
        {
            var sources = await composer.SourcesAsync(group, column, hops, ListedBy(mapped, group.Key)).ConfigureAwait(false);
            if (sources.Count > 0)
            {
                composed.Add(Direct(group, column) with { Sources = sources });
            }
        }

        var fromName = composed.SelectMany(c => c.Sources).Select(s => s.ObjectName).FirstOrDefault()
            ?? await db.LineageEdges.AsNoTracking().Where(e => e.ObjectKey == from).OrderBy(e => e.Id)
                .Select(e => e.ObjectName).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (composed.Count == 0)
        {
            notes.Add("None of the mapped columns of the target comes from the object named with 'from', within the described steps followed. Either the two are not connected by column mappings, or a step in between maps no columns.");
        }

        return TypedResults.Ok(new ColumnMappingsDto(
            to, toName, from, fromName, composed,
            Composed: composed.Any(c => c.Sources.Any(s => s.Via.Count > 0)),
            truncated || cut || composer.Truncated, notes));
    }

    /// <summary>The stored documents of the given descriptions, read back through the loader that read them at sync.</summary>
    private static async Task<Dictionary<Guid, StoredDescription>> LoadDocumentsAsync(
        CatalogDbContext db, IReadOnlyList<Guid> pipelineIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, StoredDescription>();
        for (var i = 0; i < pipelineIds.Count; i += InChunk)
        {
            var slice = pipelineIds.Skip(i).Take(InChunk).ToList();
            var rows = await db.Pipelines.AsNoTracking()
                .Where(p => slice.Contains(p.Id) && p.Kind == LineageDescriptionFlow.FlowType)
                .Select(p => new { p.Id, p.RepoId, p.Name, p.RelativePath, p.Yaml })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.Id] = new StoredDescription(row.Id, row.RepoId, row.Name, row.RelativePath, Read(row.Yaml, row.RelativePath).Flow);
            }
        }

        return result;
    }

    /// <summary>A stored description read back, or why it does not read. The catalog keeps a document's text even when
    /// it stops loading, so a description is never assumed to parse.</summary>
    private static (LineageDescriptionFlow? Flow, string? Problem) Read(string yaml, string file)
    {
        try
        {
            return (YamlLineageDescriptionFlowLoader.Parse(yaml, file), null);
        }
        catch (SqlFlowException ex)
        {
            return (null, SecretHygiene.RedactedMessage(ex));
        }
    }

    private static DescribedConnectionDto Connection(string key, string name)
    {
        var system = LineageDescribedObject.SystemOf(key);
        return new DescribedConnectionDto(key, name, system is not null, system);
    }

    /// <summary>The rows of each mapped column together, in declaration order.</summary>
    internal static List<IGrouping<ColumnGroupKey, CatalogLineageColumnMapping>> Group(IEnumerable<CatalogLineageColumnMapping> rows)
        => [.. rows.GroupBy(r => new ColumnGroupKey(r.PipelineId, r.TargetObjectKey, r.TargetColumn))];

    /// <summary>One mapped column as its description declares it. A star entry answering for a named column is shown
    /// as that column, with its sources named the same way.</summary>
    private static ColumnMappingDto Direct(IGrouping<ColumnGroupKey, CatalogLineageColumnMapping> group, string? asked)
    {
        var first = group.First();
        var captured = asked is null ? null : ColumnGlob.Capture(first.TargetColumn, asked);
        return new ColumnMappingDto(
            first.PipelineId, first.Flow, first.TargetObjectKey, first.TargetObjectName,
            captured is null ? first.TargetColumn : asked!, first.Ordinal,
            [.. group
                .Where(r => r.SourceObjectKey is not null && r.SourceColumn is not null)
                .Select(r => new ColumnSourceDto(
                    r.SourceObjectKey!, r.SourceObjectName ?? r.SourceObjectKey!,
                    captured is null ? r.SourceColumn! : ColumnGlob.Substitute(r.SourceColumn!, captured), []))],
            first.Note);
    }

    /// <summary>The columns one description maps by name on one object: what its <c>*</c> entry, standing for the
    /// columns not listed, does not stand for.</summary>
    internal static HashSet<string> ListedBy(
        IEnumerable<IGrouping<ColumnGroupKey, CatalogLineageColumnMapping>> groups, ColumnGroupKey of)
        => groups
            .Where(g => g.Key.PipelineId == of.PipelineId
                && string.Equals(g.Key.TargetObjectKey, of.TargetObjectKey, StringComparison.Ordinal)
                && !g.Key.Column.Contains('*', StringComparison.Ordinal))
            .Select(g => g.Key.Column)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record StoredDescription(Guid Id, Guid RepoId, string Name, string File, LineageDescriptionFlow? Flow);

    /// <summary>What makes rows one mapped column: the description, the written object and the column.</summary>
    internal sealed record ColumnGroupKey(Guid PipelineId, string TargetObjectKey, string Column);

    /// <summary>
    /// Follows mapped columns up through described steps until they reach one object. Each object's mappings are read
    /// once; a column already followed on a path is not followed again, so mappings that feed each other end.
    /// </summary>
    /// <remarks>
    /// A column name may carry a <c>*</c>, standing for the columns its description does not list by name. Followed
    /// into an object, such an entry is every column mapped there by name that fits it (other than the ones its own
    /// description listed, which it does not stand for), and that object's own <c>*</c> entry for the rest.
    /// </remarks>
    internal sealed class ColumnComposer(
        string from, int maxObjects, int maxVisits, int maxSources,
        Func<string, Task<List<IGrouping<ColumnGroupKey, CatalogLineageColumnMapping>>>> mappingsInto,
        CancellationToken ct)
    {
        // Node keys are case-folded when they are built. A caller's key is compared without regard to case all the
        // same, as the database compares the key of the object asked about.
        private readonly Dictionary<string, List<IGrouping<ColumnGroupKey, CatalogLineageColumnMapping>>> _read = new(StringComparer.OrdinalIgnoreCase);
        private int _visits;

        /// <summary>True when a limit (objects read, steps followed, columns followed, sources found) cut the answer
        /// short.</summary>
        public bool Truncated { get; private set; }

        /// <summary>The columns of the <c>from</c> object that <paramref name="group"/> comes from, each with the
        /// objects and columns in between. <paramref name="listed"/> is what the group's description maps by name on
        /// the same object (<see cref="ListedBy"/>).</summary>
        public async Task<List<ColumnSourceDto>> SourcesAsync(
            IGrouping<ColumnGroupKey, CatalogLineageColumnMapping> group, string? asked, int hops, IReadOnlySet<string> listed)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(listed);
            var found = new List<ColumnSourceDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var captured = asked is null ? null : ColumnGlob.Capture(group.Key.Column, asked);
            foreach (var row in group.Where(r => r.SourceObjectKey is not null && r.SourceColumn is not null))
            {
                var column = captured is null ? row.SourceColumn! : ColumnGlob.Substitute(row.SourceColumn!, captured);
                await FollowAsync(
                        row.SourceObjectKey!, row.SourceObjectName ?? row.SourceObjectKey!, column, listed, hops - 1, [], found, seen)
                    .ConfigureAwait(false);
            }

            return found;
        }

        private async Task FollowAsync(
            string objectKey, string objectName, string column, IReadOnlySet<string> listed, int hopsLeft,
            List<ColumnHopDto> via, List<ColumnSourceDto> found, HashSet<string> seen)
        {
            ct.ThrowIfCancellationRequested();
            if (++_visits > maxVisits || found.Count >= maxSources)
            {
                Truncated = true;
                return;
            }

            if (string.Equals(objectKey, from, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add($"{objectKey}\n{column}\n{string.Join('>', via.Select(v => v.ObjectKey + "." + v.Column))}"))
                {
                    found.Add(new ColumnSourceDto(objectKey, objectName, column, [.. via]));
                }

                return;
            }

            if (hopsLeft <= 0 || via.Any(v => string.Equals(v.ObjectKey, objectKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(v.Column, column, StringComparison.OrdinalIgnoreCase)))
            {
                Truncated |= hopsLeft <= 0;
                return;
            }

            if (!_read.TryGetValue(objectKey, out var groups))
            {
                if (_read.Count >= maxObjects)
                {
                    Truncated = true;
                    return;
                }

                groups = await mappingsInto(objectKey).ConfigureAwait(false);
                _read[objectKey] = groups;
            }

            foreach (var (group, followed) in Matching(groups, column, listed))
            {
                var first = group.First();
                var captured = ColumnGlob.Capture(group.Key.Column, followed) ?? string.Empty;
                var next = new List<ColumnHopDto>(via) { new(first.Flow, objectKey, objectName, followed, first.Note) };
                var listedHere = ListedBy(groups, group.Key);
                foreach (var row in group.Where(r => r.SourceObjectKey is not null && r.SourceColumn is not null))
                {
                    await FollowAsync(
                        row.SourceObjectKey!, row.SourceObjectName ?? row.SourceObjectKey!,
                        ColumnGlob.Substitute(row.SourceColumn!, captured), listedHere, hopsLeft - 1, next, found, seen).ConfigureAwait(false);
                }
            }
        }

        /// <summary>The mapped columns of an object that <paramref name="column"/> is, each with the column name it is
        /// followed as.</summary>
        private static List<(IGrouping<ColumnGroupKey, CatalogLineageColumnMapping> Group, string Followed)> Matching(
            List<IGrouping<ColumnGroupKey, CatalogLineageColumnMapping>> groups, string column, IReadOnlySet<string> listed)
        {
            static bool Star(IGrouping<ColumnGroupKey, CatalogLineageColumnMapping> g)
                => g.Key.Column.Contains('*', StringComparison.Ordinal);

            // The object's own entry for the columns it does not list, when the column fits it.
            var rest = groups.Where(g => Star(g) && ColumnGlob.Capture(g.Key.Column, column) is not null)
                .Select(g => (g, column));
            if (!column.Contains('*', StringComparison.Ordinal))
            {
                // A column by name: its own mapping, or else the entry standing for the rest.
                var named = groups
                    .Where(g => !Star(g) && string.Equals(g.Key.Column, column, StringComparison.OrdinalIgnoreCase))
                    .Select(g => (g, column))
                    .ToList();
                return named.Count > 0 ? named : [.. rest];
            }

            // A star: every column mapped here by name that fits it and that it stands for, then the rest.
            return [.. groups
                .Where(g => !Star(g) && !listed.Contains(g.Key.Column) && ColumnGlob.Capture(column, g.Key.Column) is not null)
                .Select(g => (g, g.Key.Column))
                .Concat(rest)];
        }
    }
}

/// <summary>
/// The pipelines that are flows. A lineage description (flowType: lin) is stored as a pipeline so its document has
/// one home, but it is not a flow that runs: the listings and counts of pipelines leave it out unless it is asked
/// for, so each of them holds what it held before descriptions existed.
/// </summary>
internal static class PipelineListings
{
    /// <summary>Every pipeline but the lineage descriptions.</summary>
    public static IQueryable<CatalogPipeline> WithoutLineageDescriptions(this IQueryable<CatalogPipeline> pipelines)
        => pipelines.Where(p => p.Kind != LineageDescriptionFlow.FlowType);
}

/// <summary>
/// The one wildcard a mapped column name may carry: a <c>*</c> standing for the columns not listed by name
/// (<c>*</c> alone, or with text before or after it, as in <c>raw:*</c>). Names are compared without regard to case.
/// </summary>
internal static class ColumnGlob
{
    /// <summary>What the <c>*</c> of <paramref name="pattern"/> stands for when <paramref name="name"/> fits it (empty
    /// for a pattern without one that equals the name), or null when the name does not fit.</summary>
    public static string? Capture(string pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase) ? string.Empty : null;
        }

        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        return name.Length >= prefix.Length + suffix.Length
               && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..(name.Length - suffix.Length)]
            : null;
    }

    /// <summary>The source column a pattern names once its <c>*</c> stands for <paramref name="captured"/>; a source
    /// without one is the same column for every name.</summary>
    public static string Substitute(string source, string captured)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(captured);
        return source.Replace("*", captured, StringComparison.Ordinal);
    }
}
