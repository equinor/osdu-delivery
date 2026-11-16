using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// One step of a column's trace, read from the object nearer the column asked about to the object its value comes
/// from. <c>Basis</c> says how the step is known: <c>described</c> (a lineage description's column mapping states
/// it), <c>declared</c> (the flow's own document declares how the column is made, in <c>Expression</c> and
/// <c>DataType</c>), or <c>name</c> (SQLFlow records which objects a flow reads and writes, not which columns, so
/// the column is taken to keep its name across the flow). <c>SourceColumnKnown</c> is true when the catalog's
/// column list of the source object holds the column, and null when the catalog holds no whole column list of it.
/// </summary>
public sealed record ColumnTraceHopDto(
    int Depth, string Basis, string? PipelineId, string? Flow, string? ViaModule, string? System, string? Unit,
    string ObjectKey, string ObjectName, string Column,
    string SourceObjectKey, string SourceObjectName, string SourceColumn,
    bool? SourceColumnKnown, string? Expression, string? DataType, string? Note);

/// <summary>
/// Where one path of a column's trace ends. <c>Kind</c> is <c>origin</c> (a lineage description marks the object as
/// where the data starts), <c>made</c> (a step makes the value itself), <c>flow</c> (a flow writes the object and
/// reads nothing the lineage records, so the value starts in that flow), <c>notDescribed</c> (nothing writes the
/// object: no flow, and no lineage description), <c>notMapped</c> (a lineage description writes the object, and its
/// column mapping does not cover the column), <c>derived</c> (a flow writes the object, and none of the objects it
/// reads holds a column of that name) or <c>limit</c> (the trace stopped at a limit). The last four are where
/// information is missing; <c>Detail</c> says what, in a sentence.
/// </summary>
public sealed record ColumnTraceEndDto(
    int Depth, string Kind, string ObjectKey, string ObjectName, string Column,
    string? PipelineId, string? Flow, string? System, string Detail);

/// <summary>
/// A column followed back toward its source: through the flows SQLFlow runs, and through the steps lineage
/// descriptions (flowType: lin) state. <c>Outcome</c> is <c>complete</c> when every path ends at a source,
/// <c>partial</c> when some do, and <c>missing</c> when none does. <c>Missing</c> lists, in sentences, what the
/// lineage does not say; a reader reports those as missing and never fills them in. <c>ColumnKnown</c> says whether
/// the catalog's column list of the object holds the column (null when it holds no whole column list of the object).
/// </summary>
public sealed record ColumnTraceDto(
    string ObjectKey, string ObjectName, string? ObjectKind, string Column, bool? ColumnKnown,
    string Outcome, int Depth, bool DescribedLineageUsed,
    IReadOnlyList<ColumnTraceHopDto> Hops, IReadOnlyList<ColumnTraceEndDto> Ends,
    IReadOnlyList<string> Missing, bool Truncated);

/// <summary>
/// The trace of one column back toward its source. SQLFlow's lineage is object-level, so a column is followed with
/// what the catalog holds beside it: the column mappings of lineage descriptions, what a flow's document declares
/// for a column, and each object's column list. Every step says which of them it rests on, and every path that
/// cannot be followed further ends with what is missing, so a reader is never left to guess.
/// </summary>
internal static class ColumnTrace
{
    public const string BasisDescribed = "described";
    public const string BasisDeclared = "declared";
    public const string BasisName = "name";

    public const string EndOrigin = "origin";
    public const string EndMade = "made";
    public const string EndFlow = "flow";
    public const string EndNotDescribed = "notDescribed";
    public const string EndNotMapped = "notMapped";
    public const string EndDerived = "derived";
    public const string EndLimit = "limit";

    public const string OutcomeComplete = "complete";
    public const string OutcomePartial = "partial";
    public const string OutcomeMissing = "missing";

    private const string Reads = nameof(LineageRelation.Reads);
    private const string DescribedReads = nameof(LineageRelation.DescribedReads);
    private const string DescribedWrites = nameof(LineageRelation.DescribedWrites);

    /// <summary>The tier of a column list made of what flows declare for an object: the columns they name, which
    /// need not be all it has. Such a list shows that a column is there, never that one is not; a list read from
    /// the database, or from the statement that created the table, shows both.</summary>
    private const string DeclaredTier = nameof(LineageTier.Declared);

    /// <summary>The relations that mean a flow, a module or a described step populates an object.</summary>
    private static readonly string[] WriterRelations =
        [nameof(LineageRelation.Writes), nameof(LineageRelation.Creates), DescribedWrites];

    /// <summary>How many steps a column is followed through by default, and at most.</summary>
    private const int DefaultDepth = 12;
    private const int MaxDepth = 24;

    /// <summary>The most columns followed at one depth, and the most steps and ends one answer carries. A column made
    /// from many, each made from many again, multiplies at every step; past these the answer says it was cut.</summary>
    private const int MaxFrontier = 200;
    private const int MaxHops = 400;
    private const int MaxEnds = 200;

    /// <summary>The most edges and mapped columns read for one depth.</summary>
    private const int MaxEdgesPerLevel = 4000;
    private const int MaxMappingsPerLevel = 20000;

    /// <summary>The most names an end's sentence lists before it says how many more there are.</summary>
    private const int MaxNamesListed = 6;

    private const int InChunk = 1000;
    private const int MaxKeyLength = 900;
    private const int MaxColumnLength = 512;

    private sealed record Item(string ObjectKey, string ObjectName, string Column);

    private sealed record WriterEdge(Guid? PipelineId, string? Flow, string? ViaModule, string ObjectKey, string Relation);

    /// <summary>An object a flow or a module reads. <c>ViaModule</c> names the view or procedure the read was found
    /// through, when the flow reads it only because a module it reads does.</summary>
    private sealed record ReadObject(string ObjectKey, string ObjectName, string? ViaModule);

    /// <summary>
    /// The trace of column <paramref name="column"/> of the object <paramref name="key"/>, walked breadth-first: per
    /// depth, a fixed number of set queries over the whole frontier, never a query per column.
    /// </summary>
    public static async Task<Results<Ok<ColumnTraceDto>, ProblemHttpResult>> GetAsync(
        string? key, string? column, int? depth, CatalogDbContext db, CancellationToken ct)
    {
        key = key?.Trim();
        column = column?.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(column))
        {
            return Invalid("Name the object with 'key' (an object key from a search, a dossier or a lineage walk) and its column with 'column'.");
        }

        if (key.Length > MaxKeyLength || column.Length > MaxColumnLength)
        {
            return Invalid($"'key' takes at most {MaxKeyLength} characters and 'column' at most {MaxColumnLength}.");
        }

        if (column.Contains('*', StringComparison.Ordinal))
        {
            return Invalid("'column' names one column; a '*' stands for many. Ask for one column by its name.");
        }

        var registered = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new { o.Key, o.Name, o.Kind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        string objectKey;
        string objectName;
        string? objectKind;
        if (registered is not null)
        {
            (objectKey, objectName, objectKind) = (registered.Key, registered.Name, registered.Kind);
        }
        else
        {
            // An object only lineage descriptions name has no registry row: it is known from the edges naming it.
            var described = LineageDescribedObject.IsDescribedKey(key)
                ? await db.LineageEdges.AsNoTracking()
                    .Where(e => e.ObjectKey == key && (e.Relation == DescribedReads || e.Relation == DescribedWrites))
                    .OrderBy(e => e.Id)
                    .Select(e => new { e.ObjectKey, e.ObjectName })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                : null;
            if (described is null)
            {
                return TypedResults.Problem(
                    detail: $"No object with key '{key}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
            }

            (objectKey, objectName, objectKind) = (described.ObjectKey, described.ObjectName, DescribedLineage.DescribedKind);
        }

        var levels = Math.Clamp(depth ?? DefaultDepth, 1, MaxDepth);
        var walk = new Walk(db, levels, ct);
        var columnKnown = await walk.ColumnKnownAsync(objectKey, column).ConfigureAwait(false);
        await walk.RunAsync(new Item(objectKey, objectName, column)).ConfigureAwait(false);

        var missing = new List<string>();
        if (columnKnown == false)
        {
            missing.Add($"The catalog's column list of '{objectName}' holds no column named '{column}'. The trace follows the name all the same; check the spelling.");
        }

        missing.AddRange(walk.Ends.Where(e => IsMissing(e.Kind)).Select(e => e.Detail).Distinct(StringComparer.Ordinal));
        var sources = walk.Ends.Count(e => !IsMissing(e.Kind));
        var outcome = sources == 0
            ? OutcomeMissing
            : sources == walk.Ends.Count && !walk.Truncated ? OutcomeComplete : OutcomePartial;
        return TypedResults.Ok(new ColumnTraceDto(
            objectKey, objectName, objectKind, column, columnKnown, outcome, levels, walk.DescribedUsed,
            walk.Hops, walk.Ends, missing, walk.Truncated));
    }

    /// <summary>Whether an end is one where the lineage does not say where the value comes from.</summary>
    internal static bool IsMissing(string kind)
        => kind is EndNotDescribed or EndNotMapped or EndDerived or EndLimit;

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid column trace request");

    private static string Id(string objectKey, string column) => objectKey + "\n" + column;

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Names as a sentence lists them: the first few, and how many more there are.</summary>
    private static string Listed(IReadOnlyCollection<string> names)
    {
        var shown = names.Take(MaxNamesListed).Select(n => $"'{n}'").ToList();
        var text = string.Join(", ", shown);
        return names.Count > shown.Count ? $"{text} and {names.Count - shown.Count} more" : text;
    }

    /// <summary>One trace: what it has found so far, and the queries of each depth.</summary>
    private sealed class Walk(CatalogDbContext db, int levels, CancellationToken ct)
    {
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _origins = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, DescribedLineage.StoredDescription> _documents = [];

        public List<ColumnTraceHopDto> Hops { get; } = [];

        public List<ColumnTraceEndDto> Ends { get; } = [];

        public bool Truncated { get; private set; }

        public bool DescribedUsed { get; private set; }

        /// <summary>Whether the catalog's column list of the object holds the column: true when it does, false when
        /// a whole list of the object does not, and null when the catalog holds no whole list of it.</summary>
        public async Task<bool?> ColumnKnownAsync(string objectKey, string column)
        {
            if (await db.ObjectColumns.AsNoTracking()
                    .AnyAsync(c => c.ObjectKey == objectKey && c.Name == column, ct).ConfigureAwait(false))
            {
                return true;
            }

            return await db.ObjectColumns.AsNoTracking()
                    .AnyAsync(c => c.ObjectKey == objectKey && c.Tier != DeclaredTier, ct).ConfigureAwait(false)
                ? false
                : null;
        }

        public async Task RunAsync(Item start)
        {
            _visited.Add(Id(start.ObjectKey, start.Column));
            var frontier = new List<Item> { start };
            for (var level = 1; frontier.Count > 0; level++)
            {
                if (level > levels)
                {
                    Truncated = true;
                    foreach (var item in frontier)
                    {
                        End(level - 1, EndLimit, item, null, null,
                            $"The trace stopped after {levels} steps at '{item.ObjectName}', column '{item.Column}'. Ask again from that object to go further.");
                    }

                    return;
                }

                frontier = await LevelAsync(level, frontier).ConfigureAwait(false);
            }
        }

        /// <summary>One depth: who writes each object of the frontier, and what each column there comes from.</summary>
        private async Task<List<Item>> LevelAsync(int level, List<Item> frontier)
        {
            var keys = frontier.Select(i => i.ObjectKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var writers = await db.LineageEdges.AsNoTracking()
                .Where(e => keys.Contains(e.ObjectKey) && WriterRelations.Contains(e.Relation))
                .OrderBy(e => e.Id)
                .Select(e => new WriterEdge(e.PipelineId, e.Flow, e.ViaModule, e.ObjectKey, e.Relation))
                .Take(MaxEdgesPerLevel + 1)
                .ToListAsync(ct).ConfigureAwait(false);
            Cut(writers, MaxEdgesPerLevel);

            // The flows and modules SQLFlow computed, and what each reads.
            var computed = writers.Where(w => w.Relation != DescribedWrites).ToList();
            var pipelineIds = computed.Where(w => w.PipelineId != null).Select(w => w.PipelineId!.Value).Distinct().ToList();
            var modules = computed.Where(w => w.PipelineId == null && w.ViaModule != null).Select(w => w.ViaModule!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var readsByConnector = new Dictionary<string, List<ReadObject>>(StringComparer.OrdinalIgnoreCase);
            if (pipelineIds.Count > 0 || modules.Count > 0)
            {
                var far = await LineageEndpoints.FarSideQuery(db, pipelineIds, modules, [Reads])
                    .Take(MaxEdgesPerLevel + 1)
                    .ToListAsync(ct).ConfigureAwait(false);
                Cut(far, MaxEdgesPerLevel);
                foreach (var edge in far)
                {
                    var connector = Connector(edge.PipelineId, edge.ViaModule);
                    if (!readsByConnector.TryGetValue(connector, out var list))
                    {
                        readsByConnector[connector] = list = [];
                    }

                    if (!list.Any(r => Same(r.ObjectKey, edge.ObjectKey)))
                    {
                        list.Add(new ReadObject(edge.ObjectKey, edge.ObjectName, edge.PipelineId is null ? null : edge.ViaModule));
                    }
                }
            }

            // What each flow's document declares for the columns followed.
            var columns = frontier.Select(i => i.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var declared = pipelineIds.Count == 0
                ? []
                : await db.PipelineColumns.AsNoTracking()
                    .Where(c => pipelineIds.Contains(c.PipelineId) && c.Kind == PipelineColumnKinds.Declared && columns.Contains(c.ColumnName))
                    .OrderBy(c => c.Ordinal).ThenBy(c => c.Id)
                    .ToListAsync(ct).ConfigureAwait(false);

            // The steps lineage descriptions state, with the columns they map onto the frontier.
            var describing = writers.Where(w => w.Relation == DescribedWrites && w.PipelineId != null).ToList();
            var describingIds = describing.Select(w => w.PipelineId!.Value).Distinct().ToList();
            var mappings = new List<IGrouping<DescribedLineage.ColumnGroupKey, CatalogLineageColumnMapping>>();
            var describedReads = new Dictionary<Guid, List<string>>();
            if (describingIds.Count > 0)
            {
                DescribedUsed = true;
                var stored = await db.LineageColumnMappings.AsNoTracking()
                    .Where(m => keys.Contains(m.TargetObjectKey))
                    .OrderBy(m => m.Ordinal).ThenBy(m => m.SourceOrdinal).ThenBy(m => m.Id)
                    .Take(MaxMappingsPerLevel + 1)
                    .ToListAsync(ct).ConfigureAwait(false);
                Cut(stored, MaxMappingsPerLevel);
                mappings = DescribedLineage.Group(stored);

                var unread = describingIds.Where(id => !_documents.ContainsKey(id)).ToList();
                foreach (var (id, document) in await DescribedLineage.LoadDocumentsAsync(db, unread, ct).ConfigureAwait(false))
                {
                    _documents[id] = document;
                    if (document.Flow is { } flow)
                    {
                        // An origin is marked by the description that reads the object.
                        _origins.UnionWith(flow.Reads.Where(r => r.Origin && !r.NamesFlow)
                            .Select(r => NodeKey.For(LineageDescribedObject.ServerRef(r.System ?? flow.System), null, null, r.Name!)));
                    }
                }

                var readEdges = await db.LineageEdges.AsNoTracking()
                    .Where(e => e.PipelineId != null && describingIds.Contains(e.PipelineId.Value) && e.Relation == DescribedReads)
                    .OrderBy(e => e.ObjectName).ThenBy(e => e.Id)
                    .Select(e => new { PipelineId = e.PipelineId!.Value, e.ObjectName })
                    .Take(MaxEdgesPerLevel + 1)
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var edge in readEdges.Take(MaxEdgesPerLevel))
                {
                    if (!describedReads.TryGetValue(edge.PipelineId, out var names))
                    {
                        describedReads[edge.PipelineId] = names = [];
                    }

                    names.Add(edge.ObjectName);
                }
            }

            // Which of the objects read have a whole column list in the catalog, and which of the columns followed they hold.
            var readKeys = readsByConnector.Values.SelectMany(r => r).Select(r => r.ObjectKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var wanted = columns.Concat(declared.Where(d => d.SourceColumn != null).Select(d => d.SourceColumn!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var listedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < readKeys.Count; i += InChunk)
            {
                var slice = readKeys.Skip(i).Take(InChunk).ToList();
                listedObjects.UnionWith(await db.ObjectColumns.AsNoTracking()
                    .Where(c => slice.Contains(c.ObjectKey) && c.Tier != DeclaredTier)
                    .Select(c => c.ObjectKey).Distinct()
                    .ToListAsync(ct).ConfigureAwait(false));
                var found = await db.ObjectColumns.AsNoTracking()
                    .Where(c => slice.Contains(c.ObjectKey) && wanted.Contains(c.Name))
                    .Select(c => new { c.ObjectKey, c.Name })
                    .ToListAsync(ct).ConfigureAwait(false);
                held.UnionWith(found.Select(c => Id(c.ObjectKey, c.Name)));
            }

            var next = new List<Item>();
            foreach (var item in frontier)
            {
                var own = writers.Where(w => Same(w.ObjectKey, item.ObjectKey)).ToList();
                if (own.Count == 0)
                {
                    Unwritten(level - 1, item);
                    continue;
                }

                Described(level, item, own, mappings, describedReads, next);
                Computed(level, item, own, readsByConnector, declared, listedObjects, held, next);
            }

            return next;
        }

        /// <summary>An object nothing writes: where the data starts when a description says so, and otherwise where
        /// what is recorded ends.</summary>
        private void Unwritten(int depth, Item item)
        {
            var system = LineageDescribedObject.SystemOf(item.ObjectKey);
            if (system is not null && _origins.Contains(item.ObjectKey))
            {
                End(depth, EndOrigin, item, null, system,
                    $"A lineage description marks '{item.ObjectName}' ({system}) as where the data starts.");
                return;
            }

            End(depth, EndNotDescribed, item, null, system, system is not null
                ? $"No lineage description says what writes '{item.ObjectName}' ({system}), so where its column '{item.Column}' comes from is not recorded."
                : $"No flow writes '{item.ObjectName}', and no lineage description (flowType: lin) says what does. It is where the recorded lineage starts: the source itself, or something whose producer is not written down.");
        }

        /// <summary>The steps lineage descriptions state for one column: its mapped sources, or that it is not mapped.</summary>
        private void Described(
            int level, Item item, List<WriterEdge> own,
            List<IGrouping<DescribedLineage.ColumnGroupKey, CatalogLineageColumnMapping>> mappings,
            Dictionary<Guid, List<string>> describedReads, List<Item> next)
        {
            var describing = own.Where(w => w.Relation == DescribedWrites && w.PipelineId != null)
                .GroupBy(w => w.PipelineId!.Value).Select(g => g.First()).ToList();
            if (describing.Count == 0)
            {
                return;
            }

            var onObject = mappings.Where(g => Same(g.Key.TargetObjectKey, item.ObjectKey)).ToList();
            var matched = new HashSet<Guid>();
            foreach (var (group, followed) in DescribedLineage.ColumnComposer.Matching(onObject, item.Column, EmptyNames))
            {
                var first = group.First();
                matched.Add(first.PipelineId);
                var document = _documents.GetValueOrDefault(first.PipelineId)?.Flow;
                var sources = group.Where(r => r.SourceObjectKey is not null && r.SourceColumn is not null).ToList();
                if (sources.Count == 0)
                {
                    End(level - 1, EndMade, item, (first.PipelineId, first.Flow), document?.System,
                        string.IsNullOrWhiteSpace(first.Note)
                            ? $"The described step '{first.Flow}' makes column '{item.Column}' of '{item.ObjectName}' itself; its document does not say how."
                            : $"The described step '{first.Flow}' makes column '{item.Column}' of '{item.ObjectName}' itself: {first.Note}");
                    continue;
                }

                var captured = ColumnGlob.Capture(group.Key.Column, followed) ?? string.Empty;
                foreach (var row in sources)
                {
                    var sourceColumn = ColumnGlob.Substitute(row.SourceColumn!, captured);
                    var source = new Item(row.SourceObjectKey!, row.SourceObjectName ?? row.SourceObjectKey!, sourceColumn);
                    Hop(new ColumnTraceHopDto(
                        level, BasisDescribed, first.PipelineId.ToString(), first.Flow, null, document?.System, document?.Unit,
                        item.ObjectKey, item.ObjectName, item.Column, source.ObjectKey, source.ObjectName, source.Column,
                        null, null, null, first.Note));
                    Follow(source, next);
                }
            }

            foreach (var writer in describing.Where(w => !matched.Contains(w.PipelineId!.Value)))
            {
                var id = writer.PipelineId!.Value;
                var mapsAny = onObject.Any(g => g.Key.PipelineId == id);
                var reads = describedReads.GetValueOrDefault(id) ?? [];
                var from = reads.Count == 0 ? string.Empty : $" The step reads {Listed(reads)}; which of their columns it comes from is not written down.";
                End(level - 1, EndNotMapped, item, (id, writer.Flow ?? string.Empty), _documents.GetValueOrDefault(id)?.Flow?.System,
                    mapsAny
                        ? $"The lineage description '{writer.Flow}' writes '{item.ObjectName}', and its column mapping does not cover column '{item.Column}'.{from}"
                        : $"The lineage description '{writer.Flow}' writes '{item.ObjectName}' and maps no columns.{from}");
            }
        }

        /// <summary>The flows and modules SQLFlow computed for one column: what the flow declares for it, and the
        /// objects it reads that hold a column of the name.</summary>
        private void Computed(
            int level, Item item, List<WriterEdge> own, Dictionary<string, List<ReadObject>> readsByConnector,
            List<CatalogPipelineColumn> declared, HashSet<string> listedObjects, HashSet<string> held, List<Item> next)
        {
            var connectors = own.Where(w => w.Relation != DescribedWrites && (w.PipelineId != null || w.ViaModule != null))
                .GroupBy(w => Connector(w.PipelineId, w.ViaModule), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
            foreach (var writer in connectors)
            {
                var named = writer.Flow ?? writer.ViaModule ?? string.Empty;
                var by = writer.PipelineId is { } pid ? (pid, named) : ((Guid, string)?)null;
                var how = writer.PipelineId is { } id
                    ? declared.FirstOrDefault(d => d.PipelineId == id && Same(d.ColumnName, item.Column))
                    : null;
                if (how is { SourceColumn: null } && (how.IsVirtual || how.Expression is not null))
                {
                    End(level - 1, EndMade, item, by, null,
                        how.Expression is null
                            ? $"The flow '{named}' makes column '{item.Column}' of '{item.ObjectName}' itself: its document declares it as a computed column."
                            : $"The flow '{named}' makes column '{item.Column}' of '{item.ObjectName}' itself, as: {how.Expression}");
                    continue;
                }

                // What a flow reads through a view it also reads is reached through the view, one step further on.
                var all = readsByConnector.GetValueOrDefault(Connector(writer.PipelineId, writer.ViaModule)) ?? [];
                var reads = all
                    .Where(r => !Same(r.ObjectKey, item.ObjectKey)
                        && (r.ViaModule is null || !all.Any(direct => Same(direct.ObjectKey, r.ViaModule))))
                    .ToList();
                if (reads.Count == 0)
                {
                    End(level - 1, EndFlow, item, by, null,
                        $"'{named}' writes '{item.ObjectName}' and reads no object the lineage records: the value starts in that flow, whose document says from where.");
                    continue;
                }

                var sourceColumn = how?.SourceColumn ?? item.Column;
                var holding = reads.Where(r => held.Contains(Id(r.ObjectKey, sourceColumn))).ToList();
                var unlisted = reads.Where(r => !listedObjects.Contains(r.ObjectKey)).ToList();
                var follow = holding.Count > 0 ? holding : unlisted;
                if (follow.Count == 0)
                {
                    End(level - 1, EndDerived, item, by, null,
                        $"'{named}' writes '{item.ObjectName}' from {Listed([.. reads.Select(r => r.ObjectName)])}, none of which holds a column named '{sourceColumn}' in the catalog: the flow derives or renames it. Its document says how; the lineage does not.");
                    continue;
                }

                var basis = how is null ? BasisName : BasisDeclared;
                foreach (var read in follow)
                {
                    var source = new Item(read.ObjectKey, read.ObjectName, sourceColumn);
                    Hop(new ColumnTraceHopDto(
                        level, basis, writer.PipelineId?.ToString(), writer.Flow, writer.ViaModule, null, null,
                        item.ObjectKey, item.ObjectName, item.Column, source.ObjectKey, source.ObjectName, source.Column,
                        holding.Count > 0 ? true : null, how?.Expression, how?.DataType, null));
                    Follow(source, next);
                }
            }
        }

        private static readonly HashSet<string> EmptyNames = new(StringComparer.OrdinalIgnoreCase);

        private static string Connector(Guid? pipelineId, string? viaModule)
            => pipelineId is { } id ? id.ToString() : "module:" + viaModule;

        private void Cut<T>(List<T> rows, int limit)
        {
            if (rows.Count > limit)
            {
                Truncated = true;
                rows.RemoveRange(limit, rows.Count - limit);
            }
        }

        private void Hop(ColumnTraceHopDto hop)
        {
            if (Hops.Count >= MaxHops)
            {
                Truncated = true;
                return;
            }

            Hops.Add(hop);
        }

        /// <summary>Queues a source column for the next depth, once: a column already followed is not followed again,
        /// so mappings that feed each other end.</summary>
        private void Follow(Item source, List<Item> next)
        {
            if (!_visited.Add(Id(source.ObjectKey, source.Column)))
            {
                return;
            }

            if (next.Count >= MaxFrontier)
            {
                Truncated = true;
                return;
            }

            next.Add(source);
        }

        private void End(int depth, string kind, Item item, (Guid Id, string Flow)? by, string? system, string detail)
        {
            if (Ends.Count >= MaxEnds)
            {
                Truncated = true;
                return;
            }

            Ends.Add(new ColumnTraceEndDto(
                depth, kind, item.ObjectKey, item.ObjectName, item.Column, by?.Id.ToString(), by?.Flow, system, detail));
        }
    }
}
