using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One cache flow's declaration of a type: where it takes the records from (<c>Origin</c>: osdu, table or dictionary), for an
/// OSDU type the kind and query it searches and whether it reads each record whole from storage (<c>FetchRecords</c>), for a
/// table type the connection reference and table it reads, the key column and the most rows its capture takes
/// (<c>MaxRows</c>: what the flow says, or the default), and for a dictionary type the document's file and the name of its key.
/// </summary>
public sealed record DeliveryCacheTypeSourceDto(
    string Flow, string Origin, string? Kind, string? Query, string OnChange, string? Connection = null, string? SourceObject = null, string? KeyField = null,
    string? DictionaryPath = null, bool FetchRecords = false, int? MaxRows = null);

/// <summary>One path the cache keeps for a type: the path, the name it is cached under, and the cache flows that declare it.</summary>
public sealed record DeliveryCacheFieldDto(string Path, string As, IReadOnlyList<string> Flows);

/// <summary>
/// A type of a partition's cache: the union of what its cache flows declare (each flow's kind and query, and every path any of
/// them keeps), what a change does (it waits for approval when any flow asks for that), whether its records are read whole
/// from storage (when any flow asks for that), and how many records the current version holds.
/// </summary>
public sealed record DeliveryCacheTypeDto(
    string Name, string EntityType, IReadOnlyList<DeliveryCacheTypeSourceDto> Sources, IReadOnlyList<DeliveryCacheFieldDto> Fields, string OnChange, long Items,
    string Origin = CacheOrigins.OsduText, string? Key = null, bool FetchRecords = false);

/// <summary>A schedule that refreshes a cache flow: its cadence, or that it fires behind other schedules.</summary>
public sealed record DeliveryCacheScheduleDto(Guid Id, string Name, string? Cron, int? IntervalSeconds, bool Chained);

/// <summary>
/// A cache flow that fills a partition's cache: the repository and file that define it (the file is where what it caches is
/// changed), its pipeline, the OSDU endpoint reference its OSDU types are searched on and the connection reference its table
/// types are read from (each null when it declares none of those), the schedules that refresh it, the types it declares for
/// the partition, the partitions it names (<c>partitions</c>): every partition it builds a cache for, which a refresh of it
/// names, or empty for a flow whose partition is its header's, which a refresh names none for; and the retention it declares
/// (<c>retentionDays</c>).
/// </summary>
public sealed record DeliveryCacheFlowDto(
    string Name, Guid RepoId, string RepoName, string RelativePath, Guid? PipelineId, string? Endpoint, IReadOnlyList<DeliveryCacheScheduleDto> Schedules,
    IReadOnlyList<string> Types, string? Connection = null, IReadOnlyList<string>? Partitions = null, int RetentionDays = CacheRetention.DefaultDays);

/// <summary>
/// The cache of one OSDU partition: every cache flow that fills it, the types it holds as those flows together declare them,
/// its current version with the flow and run that wrote it, how many versions it has, and its retention: how many days the
/// records of a replaced version are kept, the longest any of its cache flows declares, and how many versions keep them at
/// most, the current one counted, as an admin set it for the partition. Every delivery flow that delivers to the partition
/// reads it.
/// </summary>
public sealed record DeliveryCacheDto(
    string Scope, IReadOnlyList<DeliveryCacheFlowDto> Flows, IReadOnlyList<DeliveryCacheTypeDto> Types, DeliveryCacheVersionDto? Current, int Versions,
    int RetentionDays = CacheRetention.DefaultDays, int VersionsKept = CacheRetention.DefaultVersions);

/// <summary>
/// One cache change and what happens about it: the partition, the cached record and path that moved, the value before and
/// after, how many delivered manifest rows it reaches, how many are marked for redelivery (<c>Processed</c>), and how many,
/// of which flows, are still built from the old value (<c>Waiting</c>, <c>WaitingFlows</c>): the flows yet to run.
/// </summary>
public sealed record DeliveryUpdateTagDto(
    long TagId, string Kind, string Scope, string TypeName, string ItemId, string Path, string Change, string? OldValue, string? NewValue,
    string? FromVersion, string ToVersion, string Mode, string Status, string Summary, long AffectedRecords, long Processed,
    long Remaining, long Waiting, IReadOnlyList<DeliveryUpdateTagFlowDto> WaitingFlows, DateTime DetectedUtc, DateTime? DecidedUtc,
    string? DecidedBy, DateTime? StartedUtc, DateTime? CompletedUtc);

/// <summary>The records of one flow a cache change still waits for; the pipeline is null for a ledger no synced flow holds.</summary>
public sealed record DeliveryUpdateTagFlowDto(Guid FlowId, Guid? PipelineId, string? FlowName, long Records);

/// <summary>A decision on a set of tags: approve lets the next run carry the update, reject leaves OSDU as it is.</summary>
public sealed record DeliveryTagDecisionRequest(IReadOnlyList<long> TagIds, bool Approve);

public sealed record DeliveryTagDecisionResult(int Decided, bool Approved);

/// <summary>One cached value a record was built from, for its history page: the partition, the cached record and path, and what it held.</summary>
public sealed record DeliveryCacheUseDto(string Scope, string TypeName, string ItemId, string Path, string Kind, string Value);

/// <summary>
/// One thing delivered records of a partition were built without, and how many were: a value no cached record answered to
/// (<c>unlisted</c>), a key no row was listed under (<c>listed</c>), an id written without its record (<c>unverified</c>),
/// or a path that held nothing (<c>empty</c>). The refresh that brings it tags and redelivers them.
/// </summary>
public sealed record DeliveryCacheGapDto(string TypeName, string Path, string Kind, string Key, string Value, long Records);

/// <summary>One cached record: its OSDU id and the values captured at the declared paths, as one version of its partition's cache holds it.</summary>
public sealed record DeliveryCachedItemDto(
    long ItemId, string Scope, string Version, string TypeName, string EntityType, string RecordId, JsonElement Fields);

/// <summary>
/// The T-SQL a person runs in SSMS on the catalog database to read what the overview lists of one cached type: the records of
/// <paramref name="Version"/>, narrowed by the same search, a column per field the records listed hold
/// (<see cref="CacheProofSql.ItemsScript"/>).
/// </summary>
public sealed record DeliveryCachedItemsSqlDto(string Scope, string Version, string TypeName, string Sql);

/// <summary>
/// One type a cache version holds, how many records of it, and for a lookup table the name its key is kept under; with the
/// type's own content hash, how it compares with the version before (<c>added</c>, <c>changed</c> or <c>unchanged</c>), and
/// the version its content dates from. The version moves when anything in the partition's cache does; these move only when
/// the type does. The three are null for a version written before types were hashed, and <c>since</c> when it cannot be told.
/// </summary>
public sealed record DeliveryCacheVersionTypeDto(
    string Name, string EntityType, long Items, string? Key = null, string? Hash = null, string? Change = null, string? Since = null);

/// <summary>
/// One of the partition's system properties as a cache version holds it: a setting of the platform for the partition,
/// reported by one of its services (<c>indexer</c> or <c>search</c>), which is neither reference nor master data.
/// <c>State</c> is <c>Enabled</c>, <c>Disabled</c> or <c>Unknown</c>; <c>Detail</c> says why when it is unknown.
/// </summary>
public sealed record DeliveryCacheSystemPropertyDto(string Service, string Name, string State, string? Source, string? Detail);

/// <summary>
/// One version of a partition's cache: when it was captured, whether it is the version deliveries render against, the version
/// that was current before it, the cache flow and the run that wrote it and who asked (null run for an import from files),
/// where the content came from, what it holds, the partition's system properties the capture found, and when the cache's
/// retention pruned its records and who did (null while they are kept; a pruned version can no longer be read or compared).
/// </summary>
public sealed record DeliveryCacheVersionDto(
    string Scope, string Version, int Sequence, DateTime CapturedUtc, bool Current, string? PreviousVersion, string Flow, Guid? RunId, string CapturedBy,
    string Origin, long Items, IReadOnlyList<DeliveryCacheVersionTypeDto> Types, IReadOnlyList<DeliveryCacheSystemPropertyDto> SystemProperties,
    DateTime? PrunedUtc = null, string? PrunedBy = null);

/// <summary>
/// What changed in a partition's cache between two versions: counts per type and a page of the records that differ. The
/// counts follow the type and search filters but not the change filter.
/// </summary>
public sealed record DeliveryCacheDiffDto(
    string Scope, string FromVersion, string ToVersion, long Changed, long Added, long Removed, IReadOnlyList<DeliveryCacheDiffTypeDto> Types,
    PagedResult<DeliveryCacheDiffItemDto> Items);

/// <summary>How many records of one cached type changed, arrived and left between the two versions.</summary>
public sealed record DeliveryCacheDiffTypeDto(string TypeName, long Changed, long Added, long Removed);

/// <summary>
/// One cached record that differs between the two versions: changed, added or removed, the captured values on each side
/// (null on the side that does not hold it), and the captured names whose value moved.
/// </summary>
public sealed record DeliveryCacheDiffItemDto(
    string TypeName, string EntityType, string RecordId, string Change, JsonElement? Before, JsonElement? After, IReadOnlyList<string> ChangedFields);

/// <summary>
/// One version in a cache's history: the version captured before it, how many records it changed, added and removed against
/// that one, and which types it moved. A type that only rode along with another's change is not listed. The counts stay when
/// the cache's retention prunes either version's records; <c>BeforePruned</c> says the version before it is pruned, so the
/// records that differ can no longer be listed.
/// </summary>
public sealed record DeliveryCacheHistoryEntryDto(
    DeliveryCacheVersionDto Version, string? Before, long Changed, long Added, long Removed, IReadOnlyList<DeliveryCacheHistoryTypeDto> Types,
    bool BeforePruned = false);

/// <summary>
/// One type a version moved: <c>added</c>, <c>changed</c> or <c>removed</c>, with how many of its records changed, arrived and
/// left. Such a version is a version of the type: <c>hash</c> is the type's content hash in it (null when the version removed
/// the type, or was written before types were hashed) and <c>items</c> how many records of the type it holds.
/// </summary>
public sealed record DeliveryCacheHistoryTypeDto(string Name, string Change, long Changed, long Added, long Removed, string? Hash, long Items);

/// <summary>
/// The partition caches (osdu/docs/reference/concepts/partition-cache.md): every cache with the cache flows that fill
/// it and the types they declare, a version's records and the T-SQL that reads them, its versions, their history and
/// what changed between two, the cache changes delivered records were built from and the decisions on them, what
/// records were built without, and the cached values one record was built from.
/// </summary>
public static partial class DeliveryEndpoints
{
    /// <summary>Cached type declarations the cache listing reads at once, across every partition.</summary>
    private const int MaxCacheDefinitions = 5000;

    /// <summary>
    /// Every partition's cache: the cache flows that fill it (the repository and the file of each, which is where what it
    /// caches is changed, its pipeline and the schedules that refresh it), the types it holds as those flows together declare
    /// them with what the current version holds of each, and the current version with the flow and run that wrote it. With
    /// <paramref name="repoId"/>, the caches the repository's cache flows fill. Read-only: a cache is defined in YAML and
    /// filled by runs.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryCacheDto>>> ListCachesAsync(Guid? repoId, CatalogDbContext db, OsduDbContext osdu, CancellationToken ct)
    {
        var definitions = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .OrderBy(c => c.Scope).ThenBy(c => c.Name).ThenBy(c => c.FlowName)
            .Take(MaxCacheDefinitions)
            .ToListAsync(ct).ConfigureAwait(false);
        if (repoId is { } r)
        {
            var filled = definitions.Where(d => d.RepoId == r).Select(d => d.Scope).ToHashSet(StringComparer.Ordinal);
            definitions = definitions.Where(d => filled.Contains(d.Scope)).ToList();
        }

        if (definitions.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>([]);
        }

        var scopes = definitions.Select(d => d.Scope).Distinct().ToList();
        var flowNames = definitions.Select(d => d.FlowName).Distinct().ToList();
        var repoIds = definitions.Select(d => d.RepoId).Distinct().ToList();
        var repoNames = await RepoNamesAsync(db, repoIds, ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => repoIds.Contains(p.RepoId) && p.Kind == CacheDefinition.FlowTypeName && flowNames.Contains(p.Name))
            .Select(p => new { p.Id, p.RepoId, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var memberships = await db.ScheduleMembers.AsNoTracking()
            .Where(m => pipelineIds.Contains(m.PipelineId))
            .Select(m => new { m.PipelineId, m.ScheduleId })
            .ToListAsync(ct).ConfigureAwait(false);
        var scheduleIds = memberships.Select(m => m.ScheduleId).Distinct().ToList();
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => scheduleIds.Contains(s.Id))
            .Select(s => new DeliveryCacheScheduleDto(s.Id, s.Name, s.Cron, s.IntervalSeconds, s.Parents.Any()))
            .ToListAsync(ct).ConfigureAwait(false);
        var currentRows = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope) && v.Current)
            .ToListAsync(ct).ConfigureAwait(false);
        var versionCounts = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope))
            .GroupBy(v => v.Scope)
            .Select(g => new { Scope = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Scope, g => g.Count, StringComparer.Ordinal, ct).ConfigureAwait(false);
        var versionsKept = await osdu.DeliveryPartitions.AsNoTracking()
            .Where(p => scopes.Contains(p.Name))
            .Select(p => new { p.Name, p.CacheVersionsKept })
            .ToDictionaryAsync(p => p.Name, p => CacheRetention.BoundedVersions(p.CacheVersionsKept), StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

        var caches = definitions
            .GroupBy(d => d.Scope, StringComparer.Ordinal)
            .Select(group =>
            {
                var scope = group.Key;
                var current = currentRows.FirstOrDefault(v => v.Scope == scope) is { } row ? OsduCacheStore.Info(row) : null;
                var held = current?.Types.ToDictionary(t => t.Name, t => t.Items, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var keys = current?.Types.Where(t => t.Key is not null).ToDictionary(t => t.Name, t => t.Key, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                var flows = group
                    .GroupBy(d => (d.RepoId, d.FlowName))
                    .Select(declared =>
                    {
                        var first = declared.First();
                        var pipeline = pipelines.FirstOrDefault(p => p.RepoId == first.RepoId && p.Name == first.FlowName);
                        var refreshedBy = pipeline is null
                            ? []
                            : memberships
                                .Where(m => m.PipelineId == pipeline.Id)
                                .Join(schedules, m => m.ScheduleId, s => s.Id, (_, s) => s)
                                .OrderBy(s => s.Name, StringComparer.Ordinal)
                                .ToList();
                        // A flow that names its partitions has rows under each of them; every one it builds is listed.
                        var partitions = declared.Any(d => d.DeclaresPartitions)
                            ? definitions.Where(d => d.RepoId == first.RepoId && d.FlowName == first.FlowName).Select(d => d.Scope)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList()
                            : [];
                        return new DeliveryCacheFlowDto(
                            first.FlowName, first.RepoId, repoNames.GetValueOrDefault(first.RepoId, string.Empty), first.RelativePath, pipeline?.Id,
                            declared.Select(d => d.Endpoint).FirstOrDefault(e => e is not null), refreshedBy,
                            declared.Select(d => d.Name).Order(StringComparer.Ordinal).ToList(),
                            declared.Select(d => d.Connection).FirstOrDefault(c => c is not null),
                            partitions,
                            CacheRetention.OfPartition(declared.Select(d => d.RetentionDays)));
                    })
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();

                var types = group
                    .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(declared =>
                    {
                        var ordered = declared.OrderBy(d => d.FlowName, StringComparer.Ordinal).ToList();
                        var fields = new List<(string Path, string As, List<string> Flows)>();
                        foreach (var declaration in ordered)
                        {
                            foreach (var field in OsduCacheStore.ParseFields(declaration.FieldsJson, declaration.FlowName, declaration.Name))
                            {
                                var known = fields.FindIndex(f => f.As.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                                if (known < 0)
                                {
                                    fields.Add((field.Path, field.Name, [declaration.FlowName]));
                                }
                                else if (!fields[known].Flows.Contains(declaration.FlowName))
                                {
                                    fields[known].Flows.Add(declaration.FlowName);
                                }
                            }
                        }

                        var first = ordered[0];
                        var onChange = ordered.Any(d => d.OnChange.Equals("approve", StringComparison.OrdinalIgnoreCase)) ? "approve" : "auto";
                        // Every capture of the type reads its records whole when any flow declaring it asks for that.
                        return new DeliveryCacheTypeDto(
                            first.Name, first.EntityType,
                            ordered.Select(d => new DeliveryCacheTypeSourceDto(
                                d.FlowName, d.Origin, d.Kind, d.Query, d.OnChange, d.Connection, d.SourceObject, d.KeyField, d.DictionaryPath, d.FetchRecords,
                                CacheOrigins.Parse(d.Origin) == CacheOrigin.Table ? d.MaxRows ?? LookupKeys.DefaultTableRows : null)).ToList(),
                            fields.Select(f => new DeliveryCacheFieldDto(f.Path, f.As, f.Flows)).ToList(),
                            onChange, held.GetValueOrDefault(first.Name), first.Origin, keys.GetValueOrDefault(first.Name) ?? first.KeyField,
                            ordered.Any(d => d.FetchRecords));
                    })
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();

                return new DeliveryCacheDto(
                    scope, flows, types, current is null ? null : ToVersionDto(current), versionCounts.GetValueOrDefault(scope),
                    CacheRetention.OfPartition(group.Select(d => d.RetentionDays)),
                    versionsKept.GetValueOrDefault(scope, CacheRetention.DefaultVersions));
            })
            .ToList();
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>(caches);
    }

    /// <summary>
    /// The cached records of one partition's cache, filtered by type and searched over every value they hold, so an operator
    /// can answer "is this unit cached, and under which id". The listing reads exactly one version: <paramref name="version"/>
    /// names it, and without one it is the current version, the one delivery flows render against.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryCachedItemDto>>, ProblemHttpResult>> ListCachedItemsAsync(
        string? scope, string? type, string? search, string? version, int? page, int? pageSize, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var partition = scope.Trim();
        var resolved = await CacheVersions.ResolveAsync(osdu, partition, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return string.IsNullOrWhiteSpace(version)
                ? TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>([], p, size, 0))
                : UnknownCacheVersion(partition, version.Trim());
        }

        try
        {
            CacheVersions.ThrowIfPruned(resolved);
        }
        catch (CacheVersionPrunedException pruned)
        {
            return PrunedCacheVersion(pruned);
        }

        var (total, items) = await CachedItemsPageAsync(osdu, partition, resolved, type, search, p, size, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>(
            items.Select(i => new DeliveryCachedItemDto(i.ItemId, partition, resolved.Version, i.TypeName, i.EntityType, i.RecordId, ParseJson(i.FieldsJson))).ToList(),
            p, size, total));
    }

    /// <summary>
    /// The T-SQL that reads in SSMS what the listing above lists of one type, for the same partition, version, search and page
    /// size: its first page, a column per field those records hold, so the script's grid and the overview's line up. The
    /// script names the version it was written for, which it stops on once that version is pruned.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCachedItemsSqlDto>, ProblemHttpResult>> WriteCachedItemsSqlAsync(
        string? scope, string? type, string? search, string? version, int? pageSize, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return TypedResults.Problem(
                title: "No type", detail: "Name the cached type whose records the script reads with 'type'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var (_, size) = PageRequest.Normalize(1, pageSize);
        var partition = scope.Trim();
        var resolved = await CacheVersions.ResolveAsync(osdu, partition, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return string.IsNullOrWhiteSpace(version)
                ? TypedResults.Problem(title: "No cache", detail: $"Partition '{partition}' has no cache yet, so there are no records to read.", statusCode: StatusCodes.Status404NotFound)
                : UnknownCacheVersion(partition, version.Trim());
        }

        try
        {
            CacheVersions.ThrowIfPruned(resolved);
        }
        catch (CacheVersionPrunedException pruned)
        {
            return PrunedCacheVersion(pruned);
        }

        // The fields in the order the records listed first hold them, as the overview's grid lists its columns.
        var (_, items) = await CachedItemsPageAsync(osdu, partition, resolved, type, search, 1, size, ct).ConfigureAwait(false);
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            using var held = JsonDocument.Parse(item.FieldsJson);
            if (held.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var field in held.RootElement.EnumerateObject())
            {
                if (seen.Add(field.Name))
                {
                    fields.Add(field.Name);
                }
            }
        }

        var named = type.Trim();
        return TypedResults.Ok(new DeliveryCachedItemsSqlDto(
            partition, resolved.Version, named, CacheProofSql.ItemsScript(partition, resolved.Version, named, fields, search, size)));
    }

    /// <summary>
    /// One page of a version's cached records, narrowed to a type and to a search inside a record's id or values, in type and
    /// record order, and how many there are. The page's rows are picked by id from the index that lists a type in record
    /// order, which holds every column the listing filters and orders by, and only those rows are then read whole: a deep page
    /// of a large type skips its rows in the index instead of reading each skipped row's values.
    /// </summary>
    private static async Task<(int Total, IReadOnlyList<CachedItemRow> Items)> CachedItemsPageAsync(
        OsduDbContext osdu, string partition, DeliveryCacheVersion resolved, string? type, string? search, int page, int size, CancellationToken ct)
    {
        var query = CacheVersions.ItemsAt(osdu, partition, resolved.Sequence);
        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim();
            query = query.Where(i => i.TypeName == t);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(i => i.RecordId.Contains(term) || i.Terms.Contains(term));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var pageIds = query
            .OrderBy(i => i.TypeName).ThenBy(i => i.RecordId)
            .Skip((page - 1) * size).Take(size)
            .Select(i => i.ItemId);
        var items = await osdu.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.Scope == partition && pageIds.Contains(i.ItemId))
            .OrderBy(i => i.TypeName).ThenBy(i => i.RecordId)
            .Select(i => new CachedItemRow(i.ItemId, i.TypeName, i.EntityType, i.RecordId, i.FieldsJson))
            .ToListAsync(ct).ConfigureAwait(false);
        return (total, items);
    }

    /// <summary>What a listing reads of a cached record: what it shows, and not the search terms it is found by.</summary>
    private sealed record CachedItemRow(long ItemId, string TypeName, string EntityType, string RecordId, string FieldsJson);

    /// <summary>The versions of one partition's cache, newest first, each with the flow and run that wrote it: what the version picker offers.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheVersionDto>>, ProblemHttpResult>> ListCacheVersionsAsync(
        string? scope, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var versions = await CacheVersions.ListAsync(osdu, scope.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheVersionDto>>(versions.Select(ToVersionDto).ToList());
    }

    /// <summary>
    /// One partition cache's history, newest first: each version with the version written before it and how many records it
    /// changed, added and removed. Naming a <paramref name="type"/> narrows the counts to that type, so the versions that
    /// changed it can be told apart.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>, ProblemHttpResult>> ListCacheHistoryAsync(
        string? scope, string? type, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var history = await CacheVersions.HistoryAsync(osdu, scope.Trim(), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>(history
            .Select(h => new DeliveryCacheHistoryEntryDto(
                ToVersionDto(h.Version), h.Before, h.Changes.Changed, h.Changes.Added, h.Changes.Removed,
                h.Types.Select(t => new DeliveryCacheHistoryTypeDto(t.TypeName, t.Change, t.Counts.Changed, t.Counts.Added, t.Counts.Removed, t.Hash, t.Items)).ToList(),
                h.BeforePruned))
            .ToList());
    }

    private static DeliveryCacheVersionDto ToVersionDto(CacheVersionInfo version)
        => new(
            version.Scope, version.Version, version.Sequence, version.CapturedUtc, version.Current, version.PreviousVersion, version.FlowName, version.RunId,
            version.CapturedBy, version.Origin, version.Items,
            version.Types.Select(t => new DeliveryCacheVersionTypeDto(
                t.Name, t.EntityType, t.Items, t.Key, t.Hash, t.Change is { } change ? CacheTypeChanges.Text(change) : null, t.Since)).ToList(),
            version.SystemProperties.Select(p => new DeliveryCacheSystemPropertyDto(p.Service, p.Name, p.State.ToString(), p.Source, p.Detail)).ToList(),
            version.PrunedUtc,
            version.PrunedBy);

    private static ProblemHttpResult NoCacheNamed()
        => TypedResults.Problem(
            title: "No partition", detail: "Name the partition whose cache to read with 'scope': its data-partition-id.", statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult UnknownCacheVersion(string scope, string version)
        => TypedResults.Problem(title: "Unknown version", detail: $"The cache of partition '{scope}' holds no version '{version}'.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// A version whose records the cache's retention pruned: gone for good (410), which a client tells apart from a version
    /// the cache never held (404). The version is still listed, with what it was.
    /// </summary>
    private static ProblemHttpResult PrunedCacheVersion(CacheVersionPrunedException pruned)
        => TypedResults.Problem(title: "Version pruned", detail: pruned.Message, statusCode: StatusCodes.Status410Gone);

    /// <summary>The names of the repositories a cache answer mentions, by id.</summary>
    private static async Task<Dictionary<Guid, string>> RepoNamesAsync(CatalogDbContext db, IEnumerable<Guid> repoIds, CancellationToken ct)
    {
        var ids = repoIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Repos.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What changed in one partition's cache between two versions: per type, the records whose captured values moved, the
    /// records the later version added and the ones it no longer holds, a page at a time. <paramref name="to"/> defaults to
    /// the current version.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCacheDiffDto>, ProblemHttpResult>> CompareCacheVersionsAsync(
        string? scope, string? from, string? to, string? type, string? change, string? search, int? page, int? pageSize,
        OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return TypedResults.Problem(
                title: "No version", detail: "Name the earlier cache version to compare with 'from'.", statusCode: StatusCodes.Status400BadRequest);
        }

        CacheItemChange? kind = null;
        if (!string.IsNullOrWhiteSpace(change))
        {
            var text = change.Trim();
            if (!text.All(char.IsLetter) || !Enum.TryParse<CacheItemChange>(text, ignoreCase: true, out var parsed))
            {
                return TypedResults.Problem(
                    title: "Unknown change", detail: $"'change' is changed, added or removed, not '{text}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            kind = parsed;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = new CacheComparisonQuery(scope.Trim(), from.Trim())
        {
            ToVersion = string.IsNullOrWhiteSpace(to) ? null : to.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            Change = kind,
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Skip = (int)Math.Min(int.MaxValue, (long)(p - 1) * size),
            Take = size,
        };
        CacheComparison? diff;
        try
        {
            diff = await CacheVersions.CompareAsync(osdu, query, ct).ConfigureAwait(false);
        }
        catch (CacheVersionPrunedException pruned)
        {
            return PrunedCacheVersion(pruned);
        }

        if (diff is null)
        {
            var named = query.ToVersion is null ? $"'{query.FromVersion}' (or has no current version)" : $"'{query.FromVersion}' or '{query.ToVersion}'";
            return TypedResults.Problem(
                title: "Unknown version", detail: $"The cache of partition '{query.Scope}' holds no version {named}.", statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new DeliveryCacheDiffDto(
            diff.Scope, diff.FromVersion, diff.ToVersion, diff.Changed, diff.Added, diff.Removed,
            diff.Types.Select(t => new DeliveryCacheDiffTypeDto(t.TypeName, t.Changed, t.Added, t.Removed)).ToList(),
            new PagedResult<DeliveryCacheDiffItemDto>(
                diff.Items.Select(i => new DeliveryCacheDiffItemDto(
                    i.TypeName, i.EntityType, i.RecordId, i.Change.ToString().ToLowerInvariant(),
                    i.BeforeJson is null ? null : ParseJson(i.BeforeJson),
                    i.AfterJson is null ? null : ParseJson(i.AfterJson),
                    i.ChangedFields)).ToList(),
                p, size, diff.Total)));
    }

    /// <summary>
    /// The cache changes delivered records were built from: one row per change with what it reaches, filtered by
    /// status (pending, approved, rolling, rejected, applied) and, with <c>scope</c>, narrowed to the changes found in one
    /// partition's cache.
    /// </summary>
    private static async Task<Ok<PagedResult<DeliveryUpdateTagDto>>> ListUpdateTagsAsync(
        string? status, string? scope, int? page, int? pageSize, CatalogDbContext db, OsduDbContext osdu, ILedgerCacheSets ledger, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var tags = await ledger.ListTagsAsync(status, size, (p - 1) * size, scope, ct).ConfigureAwait(false);
        var total = await ledger.CountTagsAsync(status, scope, ct).ConfigureAwait(false);
        var pipelines = await DeliveryPipelines.ForLedgersAsync(db, osdu, tags.SelectMany(t => t.WaitingByFlow.Keys).Distinct().ToList(), ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryUpdateTagDto>(tags.Select(t => ToDto(t, pipelines)).ToList(), p, size, total));
    }

    /// <summary>
    /// What delivered records of one partition were built without, most records first, a page at a time: the flag for records
    /// that went out without a value the cache did not hold yet (a wellbore, the access group of a field), each filled by the
    /// refresh that brings it. <c>type</c> narrows to one cached type; <c>empty</c> adds the paths read that held nothing.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryCacheGapDto>>, ProblemHttpResult>> ListCacheGapsAsync(
        string? scope, string? type, bool? empty, int? page, int? pageSize, ILedgerCacheSets ledger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return TypedResults.Problem(title: "No partition", detail: "Name the partition whose cache the gaps are read in, as scope.", statusCode: StatusCodes.Status400BadRequest);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var gaps = await ledger.ListCacheGapsAsync(scope.Trim(), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), empty ?? false, size, (p - 1) * size, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryCacheGapDto>(
            gaps.Items.Select(g => new DeliveryCacheGapDto(g.TypeName, g.Path, g.Kind.ToString().ToLowerInvariant(), g.Key, g.Value, g.Records)).ToList(),
            p, size, gaps.Total));
    }

    /// <summary>Approves or rejects tags. Approving releases the records so the next run carries the new document.</summary>
    private static async Task<Results<Ok<DeliveryTagDecisionResult>, ProblemHttpResult>> DecideUpdateTagsAsync(
        DeliveryTagDecisionRequest request, ILedgerCacheSets ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || request.TagIds.Count == 0)
        {
            return TypedResults.Problem(title: "No tags", detail: "Name at least one tag to decide.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.TagIds.Count > 1000)
        {
            return TypedResults.Problem(title: "Too many tags", detail: "At most 1000 tags can be decided in one call.", statusCode: StatusCodes.Status400BadRequest);
        }

        var decided = await ledger.DecideTagsAsync(request.TagIds, request.Approve, RequestActor.Of(user) ?? "unknown", clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryTagDecisionResult(decided, request.Approve));
    }

    /// <summary>What one record read out of the cache when it was rendered, through the set it shares.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheUseDto>>, ProblemHttpResult>> ListRecordCacheUsesAsync(
        Guid flowId, Guid key, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        if (record.CacheSetId is not { } setId)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>([]);
        }

        var uses = await ledger.ListCacheSetAsync(setId, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>(uses
            .Select(u => new DeliveryCacheUseDto(u.Scope, u.TypeName, u.ItemId, u.Path, u.Kind.ToString().ToLowerInvariant(), u.ValueText))
            .ToList());
    }

    private static DeliveryUpdateTagDto ToDto(UpdateTag t, IReadOnlyDictionary<Guid, LedgerPipeline> pipelines) => new(
        t.TagId, t.Kind, t.Scope, t.TypeName, t.ItemId, t.Path, t.Change, t.OldValue, t.NewValue, t.FromVersion, t.ToVersion, t.Mode,
        t.Status, t.Describe(), t.AffectedRecords, t.Processed, t.Remaining, t.Waiting,
        t.WaitingByFlow
            .Select(f => pipelines.TryGetValue(f.Key, out var found)
                ? new DeliveryUpdateTagFlowDto(f.Key, found.Pipeline.Id, NamedFlow(found), f.Value)
                : new DeliveryUpdateTagFlowDto(f.Key, null, null, f.Value))
            .OrderByDescending(f => f.Records)
            .ThenBy(f => f.FlowName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        t.DetectedUtc, t.DecidedUtc, t.DecidedBy, t.StartedUtc, t.CompletedUtc);

    /// <summary>A flow as a change names it: the pipeline, and the interface when the ledger is one of several.</summary>
    private static string NamedFlow(LedgerPipeline found)
        => NamedInterface(found) is { } name ? $"{found.Pipeline.Name} ({name})" : found.Pipeline.Name;
}
