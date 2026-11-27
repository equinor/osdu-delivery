using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>One property of the record a column reaches: its path in the record, the type the template gives it (as the
/// mapping's outline names it), how the value comes to it, what the column does there (<c>value</c> or <c>match</c>), and
/// what is done to the value on its way.</summary>
public sealed record DeliveryColumnPropertyDto(string Path, string? Type, string How, string Role, IReadOnlyList<string> Steps);

/// <summary>Where a column's values can be profiled: the raw table the pre-ingestion flow upstream lands them in (its
/// connection reference, database, schema and name), and the column's name there.</summary>
public sealed record DeliveryColumnProfileTargetDto(string Reference, string? Database, string Schema, string Table, string Column);

/// <summary>
/// One column a delivery flow's mapping reads: the table it is in (<c>Dataset</c> null for the record table), its type as the
/// catalog holds it, the type the pre-ingestion flow upstream declares for it and the type its last run detected, that flow,
/// the properties it reaches, and the advice: the type it should have, what its type does now (<c>fits</c>, <c>parsed</c>,
/// <c>approximate</c>, <c>refused</c>, <c>unknown</c>), why, and which count of a value profile says its values convert to
/// what the properties need. <c>Profile</c> is where its values can be profiled, when lineage reaches a raw table.
/// </summary>
public sealed record DeliveryColumnTypeDto(
    string? Dataset,
    string Table,
    string Column,
    string? Type,
    bool? Nullable,
    string? DeclaredType,
    string? DetectedType,
    Guid? DeclaredIn,
    string? DeclaredInFlow,
    IReadOnlyList<DeliveryColumnPropertyDto> Properties,
    string Recommended,
    string Verdict,
    string Reason,
    string? ProfileCount,
    DeliveryColumnProfileTargetDto? Profile);

/// <summary>What a delivery flow's mapping reads, column by column, and the type each column should have: the flow, the
/// interface and partition it is read for, the mapping and the template it fills, the columns, and the problem when the
/// mapping or its template cannot be read.</summary>
public sealed record DeliveryColumnTypesDto(
    Guid PipelineId,
    string Flow,
    string? Interface,
    string? Partition,
    string Mapping,
    string? TemplateKind,
    string? TemplateVersion,
    IReadOnlyList<DeliveryColumnTypeDto> Columns,
    string? Problem);

/// <summary>
/// The type each column a delivery flow's mapping reads should have (<c>GET /flows/{pipelineId}/column-types</c>): the mapping
/// the flow pins, laid out against its template (<see cref="MappingOutline"/>), so each column is set beside the properties it
/// reaches and their types; the column's type as the catalog holds it; the types the pre-ingestion flow upstream declares and
/// detected, found by walking lineage up from the table the flow reads; and the render's own rule for each kind of property
/// (<see cref="ColumnTypeAdvice"/>). Reads no row: the profile of the values is the caller's to ask for, where
/// <c>Profile</c> says.
/// </summary>
public static class DeliveryColumnTypeEndpoints
{
    /// <summary>How many hops lineage is walked up from the table a flow reads to find the flow that declares its types.</summary>
    private const int MaxUpstreamHops = 3;

    /// <summary>The steps of a value that read or rewrite its text (the mapping's modifiers, and finding a record by it): a
    /// column they act on has to reach them as text. An expression over the row does not.</summary>
    private static readonly HashSet<string> TextSteps =
        new(["trim", "upper", "lower", "split", "replace", "equals", "date", "number", "id", "ref", "find"], StringComparer.OrdinalIgnoreCase);

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/flows/{pipelineId:guid}/column-types", ListColumnTypesAsync).WithName("ListDeliveryFlowColumnTypes");
    }

    private static async Task<Results<Ok<DeliveryColumnTypesDto>, ProblemHttpResult>> ListColumnTypesAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, [FromQuery] string? column,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, IRepositoryFolders repositories,
        CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var reference = flow.Flow.Render.Mapping;
        var (mapping, unreadable) = await SyncedMappings.ByReferenceAsync(ledger, repositories, documents, reference, ct).ConfigureAwait(false);
        if (mapping is null)
        {
            return TypedResults.Ok(Empty(flow, interfaceName, partition, reference, null, null, unreadable));
        }

        var templates = await SyncedTemplates.ReadAsync(ledger, repositories, flow.Pipeline.RepoId, ct).ConfigureAwait(false);
        var resolved = await templates.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        var outline = MappingOutline.Of(mapping, resolved.Schema);
        var templateProblem = resolved.Schema is null ? $"the mapping's template cannot be read, so the properties' types are not known: {resolved.Problem}" : null;

        var tables = Tables(flow.Flow.Source, outline);
        var reads = ColumnReads(outline, tables);
        var only = string.IsNullOrWhiteSpace(column) ? null : column.Trim();

        var columns = new List<DeliveryColumnTypeDto>();
        foreach (var table in tables.Values)
        {
            var key = DeliveryLineage.ObjectKey(flow.Flow.Source.Connection, table.Table);
            var described = (await DeliveryValueCheckEndpoints.CatalogColumnsAsync(db, table.Table, key, null, ct).ConfigureAwait(false))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var upstream = await UpstreamAsync(db, table.Table, ct).ConfigureAwait(false);

            foreach (var (name, properties) in reads.Where(r => ReferenceEquals(r.Key.Table, table)).Select(r => (r.Key.Column, r.Value)))
            {
                if (only is not null && !string.Equals(name, only, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var catalog = described.GetValueOrDefault(name);
                var advice = Advise(properties, catalog?.DataType);
                var shown = properties.Select(p => p.Property).ToList();
                columns.Add(new DeliveryColumnTypeDto(
                    table.Dataset,
                    table.Table,
                    name,
                    catalog?.DataType,
                    catalog?.Nullable,
                    upstream?.Declared.GetValueOrDefault(name),
                    upstream?.Detected.GetValueOrDefault(name),
                    upstream?.PipelineId,
                    upstream?.Flow,
                    shown,
                    advice.Recommended,
                    advice.Verdict,
                    advice.Reason,
                    advice.ProfileCount,
                    upstream?.Raw is { } raw ? raw with { Column = name } : null));
            }
        }

        return TypedResults.Ok(new DeliveryColumnTypesDto(
            flow.Pipeline.Id, flow.Pipeline.Name, Blank(interfaceName), Blank(partition), reference, mapping.Template.Kind,
            resolved.Template?.Version ?? mapping.Template.Version, columns, templateProblem));
    }

    /// <summary>One property a column reaches, whether the column has to reach it as text (a step reads its text), and whether
    /// it is only compared to find a record.</summary>
    private sealed record PropertyRead(DeliveryColumnPropertyDto Property, bool TextIn, bool MatchedOnly);

    /// <summary>One table a mapping reads: the record table (no dataset) or a child dataset of the flow.</summary>
    private sealed record SourceTable(string? Dataset, string Table);

    /// <summary>
    /// The tables of the flow the outline's sources are: the record table, and each child dataset the document reads by its
    /// name. A source the flow does not declare (the flow's parameters, the cache, another record) is no table.
    /// </summary>
    private static Dictionary<string, SourceTable> Tables(FlowSource source, MappingOutline outline)
    {
        var tables = new Dictionary<string, SourceTable>(StringComparer.Ordinal);
        foreach (var read in outline.Sources)
        {
            if (read.Kind == MappingOutlineSourceKind.Record)
            {
                tables[read.Id] = new SourceTable(null, source.Record.Object);
            }
            else if (read.Kind == MappingOutlineSourceKind.Child && source.Datasets.TryGetValue(read.Name, out var dataset))
            {
                tables[read.Id] = new SourceTable(read.Name, dataset.Object);
            }
        }

        return tables;
    }

    /// <summary>
    /// Every column the outline's values read from a table of the flow, in the order the record first reads them, with the
    /// properties each one reaches: as the value, or as what a lookup compares. A column only a <c>$when</c> or a
    /// <c>$where</c> tests reaches no property.
    /// </summary>
    private static Dictionary<(SourceTable Table, string Column), List<PropertyRead>> ColumnReads(
        MappingOutline outline, IReadOnlyDictionary<string, SourceTable> tables)
    {
        var reads = new Dictionary<(SourceTable, string), List<PropertyRead>>(new ColumnComparer());
        foreach (var node in Walk(outline.Record))
        {
            if (node.Flow is not { } flow)
            {
                continue;
            }

            foreach (var read in flow.Reads)
            {
                if (read.Role is not ("value" or "match") || !tables.TryGetValue(read.Source, out var table))
                {
                    continue;
                }

                var key = (table, read.Field);
                if (!reads.TryGetValue(key, out var properties))
                {
                    reads[key] = properties = [];
                }

                properties.Add(new PropertyRead(
                    new DeliveryColumnPropertyDto(node.Path, node.Type, node.How, read.Role, flow.Steps.Select(s => s.Label).ToList()),
                    TextIn: flow.Steps.Any(s => TextSteps.Contains(s.Kind)),
                    MatchedOnly: read.Role == "match"));
            }
        }

        return reads;

        static IEnumerable<MappingOutlineNode> Walk(IEnumerable<MappingOutlineNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in Walk(node.Children))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// The advice for a column from every property it reaches: the one property's advice, or, when the properties ask for
    /// different types, text, which each property's own rule then reads.
    /// </summary>
    private static ColumnTypeAdvice.Advice Advise(IReadOnlyList<PropertyRead> properties, string? type)
    {
        var each = properties
            .Select(p => ColumnTypeAdvice.For(p.Property.Type, type, changedOnItsWay: p.TextIn, matchedOnly: p.MatchedOnly))
            .ToList();
        if (each.Select(a => a.Recommended).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return each[0];
        }

        var kinds = string.Join(", ", properties.Select(p => $"{p.Property.Path} ({p.Property.Type ?? "type not known"})"));
        return new ColumnTypeAdvice.Advice(
            ColumnTypeAdvice.TextType,
            ColumnTypeAdvice.Verdicts.Parsed,
            $"The column reaches properties of different kinds ({kinds}): keep it as text, which each property's rule reads, or give each property a column of its own type.",
            null);
    }

    /// <summary>The flow upstream that declares a table's types, its declared and detected column types, and the raw table
    /// it lands its rows in.</summary>
    private sealed record Upstream(
        Guid PipelineId, string Flow, IReadOnlyDictionary<string, string> Declared, IReadOnlyDictionary<string, string> Detected,
        DeliveryColumnProfileTargetDto? Raw);

    /// <summary>
    /// The pre-ingestion flow whose transform types the columns of <paramref name="table"/>: walking lineage up from the table,
    /// at most <see cref="MaxUpstreamHops"/> flows, to the first flow that writes what is walked and carries transform columns
    /// (declared in its YAML or detected by its last run). The raw table it lands its rows in is the table it writes that is
    /// a table, not the view it generates. Null when lineage reaches no such flow.
    /// </summary>
    private static async Task<Upstream?> UpstreamAsync(CatalogDbContext db, string table, CancellationToken ct)
    {
        var name = SourceObjectName.Parse(table);
        var frontier = await db.Objects.AsNoTracking()
            .Where(o => o.Schema == name.Schema && o.Name == name.Name
                && (string.IsNullOrEmpty(name.Database) || o.Database == name.Database))
            .Select(o => o.Key)
            .ToListAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<string>(frontier, StringComparer.OrdinalIgnoreCase);
        const string writes = "Writes";
        const string creates = "Creates";
        const string readsRelation = "Reads";

        for (var hop = 0; hop < MaxUpstreamHops && frontier.Count > 0; hop++)
        {
            var writers = await db.LineageEdges.AsNoTracking()
                .Where(e => frontier.Contains(e.ObjectKey) && (e.Relation == writes || e.Relation == creates) && e.PipelineId != null)
                .Select(e => e.PipelineId!.Value)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            if (writers.Count == 0)
            {
                return null;
            }

            var transforms = await db.PipelineColumns.AsNoTracking()
                .Where(c => writers.Contains(c.PipelineId))
                .Select(c => new { c.PipelineId, c.Kind, c.ColumnName, c.DataType })
                .ToListAsync(ct).ConfigureAwait(false);
            if (transforms.Count > 0)
            {
                var chosen = transforms.GroupBy(c => c.PipelineId).OrderByDescending(g => g.Count()).First();
                var flow = await db.Pipelines.AsNoTracking().Where(p => p.Id == chosen.Key).Select(p => p.Name)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? chosen.Key.ToString();
                IReadOnlyDictionary<string, string> Of(string kind) => chosen
                    .Where(c => c.Kind == kind && c.DataType is not null)
                    .GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().DataType!, StringComparer.OrdinalIgnoreCase);
                return new Upstream(chosen.Key, flow, Of(PipelineColumnKinds.Declared), Of(PipelineColumnKinds.Detected),
                    await RawTableAsync(db, chosen.Key, ct).ConfigureAwait(false));
            }

            var next = await db.LineageEdges.AsNoTracking()
                .Where(e => e.PipelineId != null && writers.Contains(e.PipelineId.Value) && e.Relation == readsRelation)
                .Select(e => e.ObjectKey)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            frontier = next.Where(seen.Add).ToList();
        }

        return null;
    }

    /// <summary>The table a pre-ingestion flow lands its rows in, the one its values are profiled in: what it writes that the
    /// catalog holds as a table.</summary>
    private static async Task<DeliveryColumnProfileTargetDto?> RawTableAsync(CatalogDbContext db, Guid pipelineId, CancellationToken ct)
    {
        const string writes = "Writes";
        var written = await db.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId && e.Relation == writes)
            .Select(e => e.ObjectKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var raw = await db.Objects.AsNoTracking()
            .Where(o => written.Contains(o.Key) && o.Kind == "Table" && o.Schema != null)
            .OrderByDescending(o => o.Database != null)
            .Select(o => new { o.ServerRef, o.Database, o.Schema, o.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return raw is null ? null : new DeliveryColumnProfileTargetDto(raw.ServerRef, raw.Database, raw.Schema!, raw.Name, string.Empty);
    }

    private static DeliveryColumnTypesDto Empty(
        DeliveryEndpoints.FlowContext flow, string? interfaceName, string? partition, string mapping, string? kind, string? version, string? problem)
        => new(flow.Pipeline.Id, flow.Pipeline.Name, Blank(interfaceName), Blank(partition), mapping, kind, version, [], problem);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A column of a table, by the table's identity and the column's name in any case.</summary>
    private sealed class ColumnComparer : IEqualityComparer<(SourceTable Table, string Column)>
    {
        public bool Equals((SourceTable Table, string Column) x, (SourceTable Table, string Column) y)
            => ReferenceEquals(x.Table, y.Table) && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((SourceTable Table, string Column) obj)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Table), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }
}
