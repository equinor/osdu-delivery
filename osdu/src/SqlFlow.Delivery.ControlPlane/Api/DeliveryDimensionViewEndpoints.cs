using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Catalog;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>One join of a view: the alias its columns are read by, the dimension joined, the column joined on, and the joined table.</summary>
public sealed record DeliveryDimensionViewJoinDto(string Alias, string To, string On, string Table);

/// <summary>One column of a view: its type, the expression it is computed by, the type it is converted to, and what it holds.</summary>
public sealed record DeliveryDimensionViewColumnDto(string Name, string Type, string? Expression, string? DataType, string? Description);

/// <summary>What a check found of one join: the rows that found their row, those whose value found none, and some of those values.</summary>
public sealed record DeliveryDimensionViewJoinCheckDto(string Alias, string To, string On, long Matched, long Unmatched, IReadOnlyList<string> Examples);

/// <summary>A row whose value a column's conversion could not read: the row's number (the view's <c>id</c>) and the value.</summary>
public sealed record DeliveryDimensionViewExampleDto(long Id, string Value);

/// <summary>What a check found of one column: the rows holding a value, and for a converted column the values it could not read, with examples.</summary>
public sealed record DeliveryDimensionViewColumnCheckDto(string Name, long Values, long? Unconverted, IReadOnlyList<DeliveryDimensionViewExampleDto> Examples);

/// <summary>
/// What a build's check of a view found in one partition: <c>passed</c> with its rows, joins and columns, or <c>failed</c>
/// with the column it failed at and SQL Server's message; <c>Notes</c> says what a run's result says of it, a sentence each.
/// </summary>
public sealed record DeliveryDimensionViewCheckDto(
    long CheckId, string Partition, Guid? RunId, string Status, long Rows, IReadOnlyList<DeliveryDimensionViewJoinCheckDto> Joins,
    IReadOnlyList<DeliveryDimensionViewColumnCheckDto> Columns, string? Error, DateTime CheckedUtc, int DurationMs, IReadOnlyList<string> Notes);

/// <summary>
/// A view of a dimension flow (osdu/docs/reference/flow/dimension-views.md): as its flow declares it now
/// (<c>Declared</c>), as a build last wrote it (<c>Recorded</c>, with <c>Written</c> false when a build dropped it to
/// write it again), and whether the two differ (<c>Changed</c>: the next build writes it again). <c>PipelineId</c> is
/// the flow's pipeline, null when the catalog holds none of that name. A view a build wrote and no flow declares any
/// more (its flow gone) is listed with <c>Declared</c> false, for an admin to remove.
/// </summary>
public sealed record DeliveryDimensionViewDto(
    string Name, string ViewName, string FlowName, Guid? PipelineId, bool Declared, bool Recorded, bool Written, bool Changed, string? Description,
    string From, IReadOnlyList<DeliveryDimensionViewJoinDto> Joins, IReadOnlyList<DeliveryDimensionViewColumnDto> Columns, IReadOnlyList<string> Tables,
    string? Note, Guid? WrittenRunId, string? WrittenBy, DateTime? WrittenUtc, DeliveryDimensionViewCheckDto? LastCheck, string? Where,
    IReadOnlyList<string> GroupBy);

/// <summary>
/// A view with its newest checks, newest first, the statement a build last wrote it with (<c>Sql</c>), the one its flow's
/// document writes now (<c>DeclaredSql</c>), its item of the flow's YAML with where each thing it declares is written, and
/// what the templates the flow reads say of each join it declares (<c>JoinChecks</c>: <c>agrees</c>, <c>differs</c>, <c>unchecked</c>).
/// </summary>
public sealed record DeliveryDimensionViewDetailDto(
    DeliveryDimensionViewDto View, string? Sql, string? DeclaredSql, IReadOnlyList<DeliveryDimensionViewCheckDto> Checks, DeliveryDimensionYamlDto? Yaml,
    IReadOnlyList<DimensionViewJoinVerdict> JoinChecks);

/// <summary>
/// The joins a view whose rows are <c>From</c>'s could make, as the flow's dimensions and the templates it reads offer them, and
/// the same as the <c>join:</c> block of a view's YAML, to keep or change.
/// </summary>
public sealed record DeliveryDimensionViewSuggestionDto(string From, IReadOnlyList<DimensionViewJoinSuggestion> Joins, string Yaml);

/// <summary>What removing a view took: the view from the database when it was there, and its record with its checks.</summary>
public sealed record DeliveryDimensionViewRemovedDto(string Name, string ViewName, string FlowName, bool Dropped, long Checks, string Summary);

/// <summary>
/// The views dimension flows declare over their tables (osdu/docs/reference/flow/dimension-views.md): every view, a
/// flow's views, one view with its checks, its SQL and its YAML, and an admin's removal of a view no flow declares any
/// more. A view is named by its name, unique among the views of the database.
/// </summary>
public static class DeliveryDimensionViewEndpoints
{
    /// <summary>The most dimension flows a listing reads the declarations of.</summary>
    private const int MaxFlows = 500;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/dimensions/views", ListAsync).WithName("ListDeliveryDimensionViews");
        delivery.MapGet("/flows/{pipelineId:guid}/dimensions/views", ListFlowAsync).WithName("ListDeliveryDimensionFlowViews");
        delivery.MapGet("/dimensions/views/{name}", GetAsync).WithName("GetDeliveryDimensionView");
        delivery.MapGet("/flows/{pipelineId:guid}/dimensions/views/suggest", SuggestAsync).WithName("SuggestDeliveryDimensionViewJoins");
    }

    /// <summary>The routes that change what the module keeps of a view: removing one, an admin's alone.</summary>
    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapDelete("/dimensions/views/{name}", RemoveAsync).WithName("RemoveDeliveryDimensionView").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>Every view: those the active dimension flows declare, and those builds wrote that no flow declares any more.</summary>
    private static async Task<Ok<IReadOnlyList<DeliveryDimensionViewDto>>> ListAsync(CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active)
            .OrderBy(p => p.Name)
            .Take(MaxFlows)
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(await MergeAsync(pipelines, null, documents, ledger, ct).ConfigureAwait(false));
    }

    /// <summary>The views of one dimension flow: those it declares, and those a build of it wrote that it no longer declares.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryDimensionViewDto>>, ProblemHttpResult>> ListFlowAsync(
        Guid pipelineId, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        return string.Equals(pipeline.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase)
            ? TypedResults.Ok(await MergeAsync([pipeline], pipeline.Name, documents, ledger, ct).ConfigureAwait(false))
            : Problem(StatusCodes.Status409Conflict, "Not a dimension flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a dimension flow.");
    }

    /// <summary>One view, with its newest checks, its SQL as written and as declared, its YAML, and what the templates say of its joins.</summary>
    private static async Task<Results<Ok<DeliveryDimensionViewDetailDto>, ProblemHttpResult>> GetAsync(
        string name, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IRepositoryFolders repositories, CancellationToken ct)
    {
        var detail = await ledger.GetDimensionViewAsync(name, ct).ConfigureAwait(false);
        var flowName = detail?.View.FlowName;
        var (pipeline, declared, _) = flowName is null
            ? await FindDeclaringAsync(db, documents, name, ct).ConfigureAwait(false)
            : await DeclaringAsync(db, documents, flowName, name, ct).ConfigureAwait(false);
        IReadOnlyList<DimensionViewJoinVerdict> joinChecks = [];
        if (pipeline is not null && declared is not null)
        {
            var folders = await SyncedTemplates.ReadAsync(ledger, repositories, pipeline.RepoId, ct).ConfigureAwait(false);
            joinChecks = await DimensionViewTemplates.CheckAsync(documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath), declared, folders, ct).ConfigureAwait(false);
        }
        if (detail is null && declared is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No dimension flow declares a view named '{name}', and no build wrote one.");
        }

        var view = ToDto(declared, pipeline, detail);
        DeliveryDimensionYamlDto? yaml = null;
        if (pipeline is not null && declared is not null && DimensionYamlSource.LocateView(pipeline.Yaml, declared.Name) is { } block)
        {
            yaml = new DeliveryDimensionYamlDto(pipeline.RelativePath, block.FirstLine, block.Lines, block.Spans, block.Cut);
        }

        return TypedResults.Ok(new DeliveryDimensionViewDetailDto(
            view, detail?.View.Sql, declared?.Definition.CreateSql, (detail?.Checks ?? []).Select(ToDto).ToList(), yaml, joinChecks));
    }

    /// <summary>
    /// The joins a view whose rows are <paramref name="from"/>'s could make (<see cref="DimensionViewTemplates.SuggestAsync"/>):
    /// what a person keeps or changes in the flow's YAML; nothing is written.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionViewSuggestionDto>, ProblemHttpResult>> SuggestAsync(
        Guid pipelineId, string? from, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IRepositoryFolders repositories, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status409Conflict, "Not a dimension flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a dimension flow.");
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return Problem(StatusCodes.Status400BadRequest, "No dimension", "Name the dimension whose rows the view's rows are: from=<dimension>.");
        }

        DimensionFlowDefinition flow;
        try
        {
            flow = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
        }
        catch (FlowValidationException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Flow not readable", $"The catalog's copy of the flow does not parse: {ex.Message} Re-sync the repository.");
        }

        if (flow.Dimension(from.Trim()) is not { } dimension)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Dimension flow '{flow.Name}' has no dimension named '{from.Trim()}'.");
        }

        var folders = await SyncedTemplates.ReadAsync(ledger, repositories, pipeline.RepoId, ct).ConfigureAwait(false);
        var joins = await DimensionViewTemplates.SuggestAsync(flow, dimension.Name, folders, ct).ConfigureAwait(false);
        var yaml = joins.Count == 0
            ? "join: []"
            : "join:\n" + string.Join("\n", joins.Select(j => $"  - {{ on: {j.On}, to: {j.To}, as: {j.As} }}"));
        return TypedResults.Ok(new DeliveryDimensionViewSuggestionDto(dimension.Name, joins, yaml));
    }

    /// <summary>
    /// Removes a view no flow declares any more, for good (<see cref="DimensionViewRemoval"/>): one its flow declares is
    /// refused (its next build would write it again; take it out of the flow instead, and the build drops it), and so is one
    /// whose flow cannot be read now.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionViewRemovedDto>, ProblemHttpResult>> RemoveAsync(
        string name, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (await ledger.GetDimensionViewAsync(name, ct).ConfigureAwait(false) is not { } detail)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No build wrote a view named '{name}'.");
        }

        var view = detail.View;
        var (_, declared, unreadable) = await DeclaringAsync(db, documents, view.FlowName, view.Name, ct).ConfigureAwait(false);
        if (declared is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "Still declared",
                $"Flow {view.FlowName} declares view {view.Name}, so it is not removed: its next build would write it again. Take it out of the flow's YAML and build the flow, which drops it.");
        }

        if (unreadable is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "Flow not readable",
                $"Flow {view.FlowName} ({unreadable}) cannot be read now, so whether it still declares view {view.Name} cannot be told; nothing is removed. Fix the flow and sync the repository first.");
        }

        try
        {
            var removed = await DimensionViewRemoval.RemoveAsync(ledger, view, RequestActor.Label(user), clock, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryDimensionViewRemovedDto(removed.Name, removed.ViewName, removed.FlowName, removed.Dropped, removed.Checks, removed.Describe()));
        }
        catch (DeliveryException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Not removed", ex.Message);
        }
    }

    /// <summary>
    /// The views <paramref name="pipelines"/> declare, each with what a build recorded of it, then the views builds recorded
    /// that none of them declares: of <paramref name="flow"/> alone when it names one, of every flow otherwise.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryDimensionViewDto>> MergeAsync(
        IReadOnlyList<CatalogPipeline> pipelines, string? flow, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var recorded = await ledger.ListDimensionViewsAsync(flow, ct).ConfigureAwait(false);
        var views = new List<DeliveryDimensionViewDto>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pipeline in pipelines)
        {
            DimensionFlowDefinition parsed;
            try
            {
                parsed = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
            }
            catch (FlowValidationException)
            {
                // A flow whose catalog copy does not parse declares nothing a page can show; its board says why.
                continue;
            }

            foreach (var declared in parsed.Views)
            {
                if (!named.Add(declared.Name))
                {
                    continue;
                }

                var state = recorded.FirstOrDefault(r => string.Equals(r.Name, declared.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.FlowName, pipeline.Name, StringComparison.OrdinalIgnoreCase));
                var detail = state is null ? null : await ledger.GetDimensionViewAsync(state.Name, ct).ConfigureAwait(false);
                views.Add(ToDto(declared, pipeline, detail));
            }
        }

        foreach (var state in recorded.Where(r => !named.Contains(r.Name)))
        {
            views.Add(ToDto(null, pipelines.FirstOrDefault(p => string.Equals(p.Name, state.FlowName, StringComparison.OrdinalIgnoreCase)),
                await ledger.GetDimensionViewAsync(state.Name, ct).ConfigureAwait(false)));
        }

        return views;
    }

    /// <summary>The flow named <paramref name="flow"/> as the catalog holds it, and its declaration of the view <paramref name="name"/>; why it cannot be read, when it cannot.</summary>
    private static async Task<(CatalogPipeline? Pipeline, DimensionViewSpec? View, string? Unreadable)> DeclaringAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, string flow, string name, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Name == flow)
            .OrderByDescending(p => p.Active)
            .Take(20)
            .ToListAsync(ct).ConfigureAwait(false);
        string? unreadable = null;
        foreach (var pipeline in pipelines)
        {
            try
            {
                var parsed = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
                if (parsed.Views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) is { } view)
                {
                    return (pipeline, view, null);
                }
            }
            catch (FlowValidationException)
            {
                unreadable ??= pipeline.RelativePath;
            }
        }

        return (pipelines.FirstOrDefault(p => p.Active), null, unreadable);
    }

    /// <summary>The active dimension flow that declares a view named <paramref name="name"/>, for a view no build has written yet.</summary>
    private static async Task<(CatalogPipeline? Pipeline, DimensionViewSpec? View, string? Unreadable)> FindDeclaringAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, string name, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active)
            .OrderBy(p => p.Name)
            .Take(MaxFlows)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var pipeline in pipelines)
        {
            try
            {
                var parsed = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
                if (parsed.Views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) is { } view)
                {
                    return (pipeline, view, null);
                }
            }
            catch (FlowValidationException)
            {
                // A flow that does not parse declares no view a page can find.
            }
        }

        return (null, null, null);
    }

    private static DeliveryDimensionViewDto ToDto(DimensionViewSpec? declared, CatalogPipeline? pipeline, DimensionViewDetail? detail)
    {
        var state = detail?.View;
        var definition = declared?.Definition;
        var joins = declared is not null
            ? declared.Definition.Joins.Select(j => new DeliveryDimensionViewJoinDto(j.Alias, j.To, j.On, j.Table)).ToList()
            : (state?.Joins ?? []).Select(j => new DeliveryDimensionViewJoinDto(j.Alias, j.To, j.On, j.Table)).ToList();
        var columns = definition is not null
            ? definition.Columns.Select(c => new DeliveryDimensionViewColumnDto(c.Name, c.Type, c.Expression, c.DataType, c.Description)).ToList()
            : (state?.Columns ?? []).Select(c => new DeliveryDimensionViewColumnDto(c.Name, c.Type, c.Expression, c.DataType, c.Description)).ToList();
        return new DeliveryDimensionViewDto(
            declared?.Name ?? state!.Name,
            declared?.ViewName ?? state!.ViewName,
            pipeline?.Name ?? state!.FlowName,
            pipeline?.Id,
            declared is not null,
            state is not null,
            state?.Written ?? false,
            declared is not null && state is not null && !string.Equals(state.Sql, declared.Definition.CreateSql, StringComparison.Ordinal),
            declared?.Description ?? state?.Description,
            declared?.From ?? state?.From ?? string.Empty,
            joins,
            columns,
            definition?.Tables ?? state?.Tables ?? [],
            state?.Note,
            state?.WrittenRunId,
            state?.WrittenBy,
            state?.WrittenUtc,
            detail?.Checks.Count > 0 ? ToDto(detail.Checks[0]) : null,
            declared is not null ? declared.Where : state?.Where,
            declared is not null ? declared.GroupBy : state?.GroupBy ?? []);
    }

    private static DeliveryDimensionViewCheckDto ToDto(DimensionViewCheckState c) => new(
        c.CheckId, c.Partition, c.RunId, c.Status, c.Rows,
        c.Joins.Select(j => new DeliveryDimensionViewJoinCheckDto(j.Alias, j.To, j.On, j.Matched, j.Unmatched, j.Examples)).ToList(),
        c.Columns.Select(x => new DeliveryDimensionViewColumnCheckDto(x.Name, x.Values, x.Unconverted, x.Examples.Select(e => new DeliveryDimensionViewExampleDto(e.Id, e.Value)).ToList())).ToList(),
        c.Error, c.CheckedUtc, c.DurationMs, c.Notes());

    private static ProblemHttpResult Problem(int status, string title, string detail) => TypedResults.Problem(detail, statusCode: status, title: title);
}
