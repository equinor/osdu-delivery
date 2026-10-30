using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>The search terms of one entity type, or of every one.</summary>
public sealed record DeliverySearchTermsDto(string? EntityType, IReadOnlyList<SearchTermView> Terms);

/// <summary>An entity type search terms are extracted for, with how many it has.</summary>
public sealed record DeliverySearchTermTypeDto(string EntityType, int Terms);

/// <summary>What a person makes of a search term, as the GUI sends it: a name (empty for the column's own), whether it is deleted, the route, a note.</summary>
public sealed record DeliverySearchTermRefinementDto(string? Name = null, bool Excluded = false, string? Route = null, string? Note = null);

/// <summary>Search terms to delete or restore, by id, as the GUI sends them, with the entity type they are listed for.</summary>
public sealed record DeliverySearchTermBatchDto(IReadOnlyList<Guid>? Terms = null, string? EntityType = null);

/// <summary>
/// The search terms (osdu/docs/reference/concepts/search-terms.md): the columns of the source systems the mappings of
/// active delivery flows read, extracted by the repository sync, with every route by which each reaches the records,
/// and what people made of them. Listing them is any signed-in reader's; refining, deleting and restoring them changes
/// what every reader searches by, so each is an author action.
/// </summary>
public static class DeliverySearchTermEndpoints
{
    public static RouteGroupBuilder MapDeliverySearchTermReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapGet("/search-terms", ListAsync).WithName("ListDeliverySearchTerms");
        delivery.MapGet("/search-terms/entity-types", EntityTypesAsync).WithName("ListDeliverySearchTermTypes");
        delivery.MapGet("/search-terms/{termId:guid}", GetAsync).WithName("GetDeliverySearchTerm");
        return group;
    }

    public static RouteGroupBuilder MapDeliverySearchTermAuthorEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapPut("/search-terms/{termId:guid}", RefineAsync).WithName("RefineDeliverySearchTerm");
        delivery.MapDelete("/search-terms/{termId:guid}/refinement", ResetAsync).WithName("ResetDeliverySearchTerm");
        delivery.MapPost("/search-terms/delete", DeleteAsync).WithName("DeleteDeliverySearchTerms");
        delivery.MapPost("/search-terms/restore", RestoreAsync).WithName("RestoreDeliverySearchTerms");
        return group;
    }

    /// <summary>
    /// The terms of an entity type, named directly or by a kind pattern that names one (<c>*:*:work-product-component--WellLog:*</c>),
    /// or of every entity type; with <c>orphans=true</c>, the refinements whose terms no pipeline gives any longer too. Named by a
    /// kind (<c>osdu:wks:work-product-component--WellLog:1.5.0</c>), each term is searched through its route for that kind.
    /// </summary>
    private static async Task<Results<Ok<DeliverySearchTermsDto>, ProblemHttpResult>> ListAsync(
        [FromQuery] string? entityType, [FromQuery] string? kind, [FromQuery] bool? orphans,
        OsduDbContext osdu, DeliveryDocumentLoader documents, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
    {
        string? type = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (ExplorerKinds.Problem(kind.Trim()) is { } wrong)
            {
                return DeliveryEndpoints.Invalid(wrong);
            }

            // A kind that leaves the type open (every type, a group) has no terms of its own: its records are of many types.
            type = ExplorerKinds.EntityTypeOf(kind.Trim());
            if (type is null)
            {
                return TypedResults.Ok(new DeliverySearchTermsDto(null, []));
            }
        }
        else if (!string.IsNullOrWhiteSpace(entityType))
        {
            type = entityType.Trim();
        }

        var terms = await new SearchTermDirectory(osdu, documents, clock, folders).ListAsync(type, string.IsNullOrWhiteSpace(kind) ? null : kind.Trim(), orphans ?? false, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliverySearchTermsDto(type, terms));
    }

    private static async Task<Ok<IReadOnlyList<DeliverySearchTermTypeDto>>> EntityTypesAsync(
        OsduDbContext osdu, DeliveryDocumentLoader documents, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
    {
        var types = await new SearchTermDirectory(osdu, documents, clock, folders).EntityTypesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliverySearchTermTypeDto>>(types.Select(t => new DeliverySearchTermTypeDto(t.EntityType, t.Terms)).ToList());
    }

    /// <summary>One term as it reaches <c>entityType</c> (the first type it reaches when left out).</summary>
    private static async Task<Results<Ok<SearchTermView>, NotFound>> GetAsync(
        Guid termId, [FromQuery] string? entityType, OsduDbContext osdu, DeliveryDocumentLoader documents, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
    {
        var term = await new SearchTermDirectory(osdu, documents, clock, folders).FindAsync(termId, Blank(entityType), null, ct).ConfigureAwait(false);
        return term is null ? TypedResults.NotFound() : TypedResults.Ok(term);
    }

    /// <summary>Keeps a name, whether the term is left out, the route it is searched through and a note; a request keeping nothing removes the refinement.</summary>
    private static async Task<Results<Ok<SearchTermView>, NotFound, ProblemHttpResult>> RefineAsync(
        Guid termId, [FromQuery] string? entityType, DeliverySearchTermRefinementDto? body, OsduDbContext osdu, DeliveryDocumentLoader documents,
        TimeProvider clock, IRepositoryFolders folders, ClaimsPrincipal user, CancellationToken ct)
    {
        var directory = new SearchTermDirectory(osdu, documents, clock, folders);
        if (await directory.FindAsync(termId, Blank(entityType), null, ct).ConfigureAwait(false) is null)
        {
            return TypedResults.NotFound();
        }

        var request = body ?? new DeliverySearchTermRefinementDto();
        try
        {
            var refined = await directory.RefineAsync(
                termId, Blank(entityType), new SearchTermRefinementRequest(request.Name, request.Excluded, request.Route, request.Note), RequestActor.Label(user), ct).ConfigureAwait(false);
            return TypedResults.Ok(refined);
        }
        catch (DeliveryException ex)
        {
            return DeliveryEndpoints.Invalid(ex.Message);
        }
    }

    /// <summary>
    /// Deletes the terms named, in one save: one a pipeline reads is taken out of the search and listed as deleted until
    /// restored, since the next sync extracts it again; one no pipeline reads any longer has its refinement removed for good.
    /// </summary>
    private static async Task<Results<Ok<SearchTermDeletion>, ProblemHttpResult>> DeleteAsync(
        DeliverySearchTermBatchDto? body, OsduDbContext osdu, DeliveryDocumentLoader documents, TimeProvider clock, IRepositoryFolders folders, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var deleted = await new SearchTermDirectory(osdu, documents, clock, folders)
                .DeleteAsync(body?.Terms ?? [], Blank(body?.EntityType), RequestActor.Label(user), ct).ConfigureAwait(false);
            return TypedResults.Ok(deleted);
        }
        catch (DeliveryException ex)
        {
            return DeliveryEndpoints.Invalid(ex.Message);
        }
    }

    /// <summary>Offers the deleted terms named in the explorer again, with the name, note and route each had, in one save.</summary>
    private static async Task<Results<Ok<SearchTermRestoration>, ProblemHttpResult>> RestoreAsync(
        DeliverySearchTermBatchDto? body, OsduDbContext osdu, DeliveryDocumentLoader documents, TimeProvider clock, IRepositoryFolders folders, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var restored = await new SearchTermDirectory(osdu, documents, clock, folders)
                .RestoreAsync(body?.Terms ?? [], RequestActor.Label(user), ct).ConfigureAwait(false);
            return TypedResults.Ok(restored);
        }
        catch (DeliveryException ex)
        {
            return DeliveryEndpoints.Invalid(ex.Message);
        }
    }

    /// <summary>Text given, trimmed; null for none.</summary>
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Removes what people made of the term: it is searched as its pipelines give it again, or, when no pipeline gives it, it is gone.</summary>
    private static async Task<Results<NoContent, NotFound>> ResetAsync(
        Guid termId, OsduDbContext osdu, DeliveryDocumentLoader documents, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
    {
        var removed = await new SearchTermDirectory(osdu, documents, clock, folders).ResetAsync(termId, ct).ConfigureAwait(false);
        return removed ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
