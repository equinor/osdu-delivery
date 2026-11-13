using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// An operator's purge of a partition cache's history: <c>Scope</c> names the partition, <c>KeepDays</c> how many days of
/// replaced versions keep their records, from 0 (only the versions that are always kept) to 36500, and
/// <c>VersionsKept</c> how many versions keep them at most, the current one counted, from 2 to 1000; each left out, the
/// partition's own.
/// </summary>
public sealed record DeliveryCachePruneRequest(string? Scope, int? KeepDays, int? VersionsKept = null);

/// <summary>
/// How many versions of a partition's cache keep their records, the current one counted, as an admin sets it for the
/// partition: <c>Scope</c> names the partition, <c>VersionsKept</c> the number, from 2 to 1000.
/// </summary>
public sealed record DeliveryCacheRetentionRequest(string? Scope, int? VersionsKept);

/// <summary>
/// What a purge of a partition cache's history did, or for a preview would do: the days it kept and the most versions it
/// let keep their records, how many versions keep them, the versions it prunes (oldest first) and the stored rows that go,
/// what it waits for when it cannot prune yet, and one line saying it all.
/// </summary>
public sealed record DeliveryCachePruneResultDto(
    string Scope, int KeepDays, bool Preview, int Kept, IReadOnlyList<string> Pruned, long Rows, string? Deferred, string Summary,
    int VersionsKept = CacheRetention.DefaultVersions);

/// <summary>
/// The Cache page's purge of a partition cache's history, and the number of versions it keeps
/// (osdu/docs/reference/concepts/partition-cache.md, Retention). A purge is the retention every refresh applies, with the
/// days and versions an operator names in place of the partition's: the current version, the one it replaced and every
/// version a delivery flow pins keep their records whatever the days and versions, a pruned version stays listed with what
/// it changed, and it records who pruned it and when. A preview says what it would prune, and changes nothing; the purge,
/// and setting how many versions the partition keeps, are an admin's, since what they remove cannot be brought back.
/// </summary>
public static class DeliveryCachePruneEndpoints
{
    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/cache/prune/preview", PreviewAsync).WithName("PreviewDeliveryCachePrune");
        delivery.MapPost("/cache/prune", PruneAsync).WithName("PruneDeliveryCacheHistory").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapPut("/cache/retention", KeepVersionsAsync).WithName("KeepDeliveryCacheVersions").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>
    /// Sets how many versions of the partition's cache keep their records, as the requesting admin, and applies it at once:
    /// the versions past the number lose their records now rather than at the next refresh, which applies it from then on.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> KeepVersionsAsync(
        DeliveryCacheRetentionRequest request, DeliveryPartitionRegistry registry, ICacheStore caches, TimeProvider clock, ILoggerFactory loggers,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Scope))
        {
            return Problem(StatusCodes.Status400BadRequest, "No partition", "Name the partition whose cache to keep versions of with 'scope': its data-partition-id.");
        }

        if (request.VersionsKept is not { } versions || !CacheRetention.KeepsVersions(versions))
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Versions out of range",
                CacheRetention.VersionsOutOfRange(request.VersionsKept is { } given ? given.ToString(CultureInfo.InvariantCulture) : "none"));
        }

        var scope = request.Scope.Trim();
        var actor = RequestActor.Label(user);
        var now = clock.GetUtcNow();
        try
        {
            await registry.KeepCacheVersionsAsync(scope, versions, actor, now.UtcDateTime, ct).ConfigureAwait(false);
        }
        catch (PartitionNotRegisteredException missing)
        {
            return Problem(StatusCodes.Status404NotFound, "Not registered", missing.Message);
        }

        var keptFrom = string.Create(CultureInfo.InvariantCulture, $"The cache of partition '{scope}' keeps {versions} versions from now on");
        var logger = loggers.CreateLogger(typeof(DeliveryCachePruneEndpoints).FullName!);
        var days = (await caches.DeclarationAsync(scope, ct).ConfigureAwait(false)).RetentionDays;
        if (await caches.CurrentVersionAsync(scope, ct).ConfigureAwait(false) is null)
        {
            LogKept(logger, actor, scope, versions, "the cache holds no version yet");
            return TypedResults.Ok(new DeliveryCachePruneResultDto(
                scope, days, Preview: false, 0, [], 0, Deferred: null, $"{keptFrom}; it holds no version yet.", versions));
        }

        var outcome = await caches.ApplyRetentionAsync(
            CacheRetentionRequest.Purge(scope, days, now, actor, dryRun: false, versionsKept: versions), ct).ConfigureAwait(false);
        var summary = $"{keptFrom}. {outcome.DescribePurge()}";
        LogKept(logger, actor, scope, versions, summary);
        return TypedResults.Ok(new DeliveryCachePruneResultDto(
            scope, days, Preview: false, outcome.Kept, outcome.Pruned, outcome.RowsRemoved, outcome.Deferred, summary, outcome.VersionsKept));
    }

    private static void LogKept(ILogger logger, string actor, string scope, int versions, string summary)
        => logger.LogInformation(
            "{Actor} set the cache of partition {Scope} to keep {Versions} version(s): {Summary}", actor, scope, versions, summary);

    /// <summary>What a purge keeping <c>KeepDays</c> would prune of the partition's cache; nothing changes.</summary>
    private static Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> PreviewAsync(
        DeliveryCachePruneRequest request, ICacheStore caches, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
        => ApplyAsync(request, caches, clock, user, dryRun: true, logger: null, ct);

    /// <summary>Prunes the partition cache's history, keeping <c>KeepDays</c>, as the requesting admin.</summary>
    private static Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> PruneAsync(
        DeliveryCachePruneRequest request, ICacheStore caches, TimeProvider clock, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
        => ApplyAsync(request, caches, clock, user, dryRun: false, loggers.CreateLogger(typeof(DeliveryCachePruneEndpoints).FullName!), ct);

    private static async Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> ApplyAsync(
        DeliveryCachePruneRequest request, ICacheStore caches, TimeProvider clock, ClaimsPrincipal user, bool dryRun, ILogger? logger, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Scope))
        {
            return Problem(StatusCodes.Status400BadRequest, "No partition", "Name the partition whose cache history to prune with 'scope': its data-partition-id.");
        }

        var scope = request.Scope.Trim();
        if (request.KeepDays is < 0 or > CacheRetention.MaxDays)
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Days out of range",
                string.Create(CultureInfo.InvariantCulture, $"'keepDays' is {request.KeepDays}; a purge keeps from 0 (only the versions that are always kept) to {CacheRetention.MaxDays} days."));
        }

        if (request.VersionsKept is { } versions && !CacheRetention.KeepsVersions(versions))
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Versions out of range", CacheRetention.VersionsOutOfRange(versions.ToString(CultureInfo.InvariantCulture)));
        }

        if (await caches.CurrentVersionAsync(scope, ct).ConfigureAwait(false) is null)
        {
            return Problem(StatusCodes.Status404NotFound, "No cache", $"The cache of partition '{scope}' holds no version, so it has no history to prune.");
        }

        var keepDays = request.KeepDays ?? (await caches.DeclarationAsync(scope, ct).ConfigureAwait(false)).RetentionDays;
        var actor = RequestActor.Label(user);
        var outcome = await caches.ApplyRetentionAsync(
            CacheRetentionRequest.Purge(scope, keepDays, clock.GetUtcNow(), actor, dryRun, request.VersionsKept), ct).ConfigureAwait(false);
        var summary = outcome.DescribePurge();
        if (logger is not null)
        {
            logger.LogInformation(
                "{Actor} purged the history of the cache of partition {Scope}, keeping {Rule}: {Summary}", actor, scope, outcome.Rule, summary);
        }

        return TypedResults.Ok(new DeliveryCachePruneResultDto(
            scope, keepDays, dryRun, outcome.Kept, outcome.Pruned, outcome.RowsRemoved, outcome.Deferred, summary, outcome.VersionsKept));
    }

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
