using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One registered access restriction (osdu/docs/reference/concepts/access-restriction.md, The registry): the partition it
/// belongs to, its name and what it is for, the groups a restricted record's ACL in OSDU becomes, and how many records carry
/// it now.
/// </summary>
public sealed record DeliveryAccessRestrictionDto(
    int RestrictionId, string Partition, string Name, string? Description, IReadOnlyList<string> Viewers, IReadOnlyList<string> Owners, long Records,
    DateTime CreatedUtc, string CreatedBy, DateTime UpdatedUtc, string UpdatedBy)
{
    internal static DeliveryAccessRestrictionDto Of(RegisteredAccessRestriction registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        var r = registered.Restriction;
        return new DeliveryAccessRestrictionDto(
            r.RestrictionId, r.Partition, r.Name, r.Description, r.Acl.Viewers, r.Acl.Owners, registered.Records, r.CreatedUtc, r.CreatedBy, r.UpdatedUtc, r.UpdatedBy);
    }
}

/// <summary>An access restriction to register: its partition, name, what it is for, and the groups it sets as viewers and owners.</summary>
public sealed record DeliveryAccessRestrictionAddRequest(string? Partition, string? Name, string? Description, IReadOnlyList<string>? Viewers, IReadOnlyList<string>? Owners);

/// <summary>What an access restriction is for; empty clears it. Its groups stay as registered.</summary>
public sealed record DeliveryAccessRestrictionDescribeRequest(string? Description);

/// <summary>
/// The access restriction registry: reading it is anyone's (the removal dialog lists a partition's restrictions to pick
/// from), registering, describing and removing one is admin work. A restriction's groups never change once registered,
/// since OSDU holds them on the records restricted under it; one records carry is not removed (409).
/// </summary>
public static partial class DeliveryAccessRestrictionEndpoints
{
    /// <summary>Maps the registry under the module's group.</summary>
    public static void Map(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/access-restrictions", ListAsync).WithName("ListDeliveryAccessRestrictions");
        delivery.MapPost("/access-restrictions", AddAsync).WithName("RegisterDeliveryAccessRestriction").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapPut("/access-restrictions/{restrictionId:int}", DescribeAsync).WithName("DescribeDeliveryAccessRestriction").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapDelete("/access-restrictions/{restrictionId:int}", RemoveAsync).WithName("RemoveDeliveryAccessRestriction").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>The restrictions of one partition, or of every partition, by partition then name.</summary>
    private static async Task<Ok<IReadOnlyList<DeliveryAccessRestrictionDto>>> ListAsync([FromQuery] string? partition, OsduDbContext osdu, CancellationToken ct)
    {
        var listed = await AccessRestrictionRegistry.ListAsync(osdu, string.IsNullOrWhiteSpace(partition) ? null : partition.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAccessRestrictionDto>>([.. listed.Select(DeliveryAccessRestrictionDto.Of)]);
    }

    private static async Task<Results<Created<DeliveryAccessRestrictionDto>, ProblemHttpResult>> AddAsync(
        DeliveryAccessRestrictionAddRequest? request, OsduDbContext osdu, TimeProvider clock, ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                detail: "An access restriction to register names its partition, its name and the groups it sets as viewers and owners.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var actor = RequestActor.Label(user);
        AccessRestrictionState added;
        try
        {
            added = await AccessRestrictionRegistry.AddAsync(
                osdu, request.Partition, request.Name, request.Description, request.Viewers, request.Owners, actor, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }
        catch (DeliveryException invalid)
        {
            return TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Not registered");
        }

        LogChanged(Logger(loggers), "registered", added.Name, added.Partition, actor);
        return TypedResults.Created(
            $"access-restrictions/{added.RestrictionId}", DeliveryAccessRestrictionDto.Of(new RegisteredAccessRestriction(added, 0)));
    }

    private static async Task<Results<Ok<DeliveryAccessRestrictionDto>, ProblemHttpResult>> DescribeAsync(
        int restrictionId, DeliveryAccessRestrictionDescribeRequest? request, OsduDbContext osdu, TimeProvider clock, ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        var actor = RequestActor.Label(user);
        AccessRestrictionState? described;
        try
        {
            described = await AccessRestrictionRegistry.DescribeAsync(osdu, restrictionId, request?.Description, actor, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }
        catch (DeliveryException invalid)
        {
            return TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (described is null)
        {
            return NotFound(restrictionId);
        }

        LogChanged(Logger(loggers), "described", described.Name, described.Partition, actor);
        var registered = await AccessRestrictionRegistry.GetAsync(osdu, restrictionId, ct).ConfigureAwait(false);
        return TypedResults.Ok(DeliveryAccessRestrictionDto.Of(registered ?? new RegisteredAccessRestriction(described, 0)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        int restrictionId, OsduDbContext osdu, ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        var registered = await AccessRestrictionRegistry.GetAsync(osdu, restrictionId, ct).ConfigureAwait(false);
        if (registered is null)
        {
            return NotFound(restrictionId);
        }

        try
        {
            if (!await AccessRestrictionRegistry.RemoveAsync(osdu, restrictionId, ct).ConfigureAwait(false))
            {
                return NotFound(restrictionId);
            }
        }
        catch (AccessRestrictionInUseException inUse)
        {
            return TypedResults.Problem(detail: inUse.Message, statusCode: StatusCodes.Status409Conflict, title: "Restriction in use");
        }

        LogChanged(Logger(loggers), "removed", registered.Restriction.Name, registered.Restriction.Partition, RequestActor.Label(user));
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult NotFound(int restrictionId)
        => TypedResults.Problem(
            detail: $"No access restriction is registered with id {restrictionId}.", statusCode: StatusCodes.Status404NotFound, title: "Unknown access restriction");

    private static ILogger Logger(ILoggerFactory loggers) => loggers.CreateLogger("SqlFlow.Delivery.AccessRestrictions");

    [LoggerMessage(Level = LogLevel.Information, Message = "Access restriction {Name} of partition {Partition} {Change} by {Actor}.")]
    private static partial void LogChanged(ILogger logger, string change, string name, string partition, string actor);
}
