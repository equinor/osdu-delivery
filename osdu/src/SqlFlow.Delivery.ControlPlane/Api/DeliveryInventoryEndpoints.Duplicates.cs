using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>One property of a duplicate check's key: its path from the record's root, and how its values compare (<c>exact</c>, <c>ignoreCase</c> or <c>loose</c>).</summary>
public sealed record DeliveryDuplicateKeyPropertyDto(string? Path, string? Compare);

/// <summary>
/// A duplicate check an operator asks of an inventory's ids (osdu/docs/reference/flow/inventory-duplicates.md):
/// the key the copies of a record hold alike, whether to read OSDU again rather than take the check already made of the same key
/// against the same build (<c>refresh</c>), and the pool its run is queued on.
/// </summary>
public sealed record DeliveryInventoryDuplicatesRequest(IReadOnlyList<DeliveryDuplicateKeyPropertyDto>? Key, bool? Refresh = null, string? Pool = null);

/// <summary>How many of a check's groups came out one way.</summary>
public sealed record DeliveryDuplicateOutcomeCountDto(string Outcome, long Groups);

/// <summary>
/// One duplicate check of an inventory: its key, where it stands (queued, running and what it reads, completed, failed or
/// expired) and whether the inventory was built again since (<c>Stale</c>), what it had to say, what it read (the ids the
/// inventory served, the records the search returned, the ids holding a key and holding none, records outside the inventory,
/// ids changed since the build), what it found (groups, the ids in them, how many it judged, decided and left for review, the
/// copies picked to remove, each outcome's groups once it completed), the requests it sent, the selection last made of its
/// picks and whether a choice since leaves it behind (<c>SelectionCurrent</c>), its run, who asked and when, and why it failed.
/// </summary>
public sealed record DeliveryInventoryDuplicateCheckDto(
    long CheckId, int InventoryId, string Partition, string Status, bool Stale, string? Phase, IReadOnlyList<DeliveryDuplicateKeyPropertyDto> Key,
    IReadOnlyList<string> Notes, long Candidates, long Checked, long Keyed, long Unkeyed, long Outside, long Changed, long Groups, long Copies, long GroupsJudged,
    long Decided, long Review, long ToRemove, long Requests, IReadOnlyList<DeliveryDuplicateOutcomeCountDto>? Outcomes, long? SelectionId, bool SelectionCurrent,
    DateTime? ChosenUtc, Guid? RunId, string Actor, DateTime CreatedUtc, DateTime? StartedUtc, DateTime? CompletedUtc, string? Error);

/// <summary>
/// One copy of a group of duplicates: the inventory's row of it now, the version the index held when the check read its key and
/// what it held there, what storage answered for it and at which version, how many values it holds under <c>data</c>, how many
/// records name it, and its role in its group with why.
/// </summary>
public sealed record DeliveryDuplicateCopyDto(
    DeliveryInventoryRecordDto Record, long? IndexedVersion, IReadOnlyDictionary<string, IReadOnlyList<string>>? Values, string? StorageState, long? StoredVersion,
    int? Attributes, long? MentionedBy, string? Role, string? Reason);

/// <summary>
/// One group of ids a check found sharing a key: its number, how many copies, the values they share, how it came out and why,
/// the copy it keeps, who chose it and when (for a copy an operator chose), and its copies, when it holds few enough to list
/// with it (else they are paged on their own).
/// </summary>
public sealed record DeliveryDuplicateGroupDto(
    int GroupNumber, int Copies, IReadOnlyDictionary<string, IReadOnlyList<string>>? Values, string Outcome, string? Reason, long? KeptInventoryRecordId,
    string? ChosenBy, DateTime? ChosenUtc, IReadOnlyList<DeliveryDuplicateCopyDto>? Members);

/// <summary>A page of a check's groups, with the group the next page starts after; left out on the last page.</summary>
public sealed record DeliveryDuplicateGroupPageDto(IReadOnlyList<DeliveryDuplicateGroupDto> Items, int? Next);

/// <summary>A page of one group's copies, with the inventory row the next page starts after; left out on the last page.</summary>
public sealed record DeliveryDuplicateCopyPageDto(IReadOnlyList<DeliveryDuplicateCopyDto> Items, long? Next);

/// <summary>An operator's choice of the copy a group keeps, by its inventory row; null takes a choice back, leaving the group to the check's own pick.</summary>
public sealed record DeliveryDuplicateKeepRequest(long? InventoryRecordId);

public static partial class DeliveryInventoryEndpoints
{
    /// <summary>The checks an inventory's history lists when the request names no limit, and the most it lists.</summary>
    private const int DefaultChecks = 20;

    private const int MaxChecks = 100;

    /// <summary>The groups a page lists when the request names no limit, and the most a page lists.</summary>
    private const int DefaultGroups = 50;

    private const int MaxGroups = 500;

    /// <summary>
    /// Queues a check of an inventory's ids for duplicate records, as the caller (osdu/docs/reference/flow/inventory-duplicates.md, Finding
    /// duplicate records): the key is checked, a check is opened and filled by a duplicates run of the inventory flow, through
    /// its own source. The same key asked again of the same build answers with the check made of it already (200), unless
    /// <c>refresh</c> asks for OSDU to be read again; a new check answers 202. Refused with 400 for a key that is not one, and
    /// with 409 when the inventory was never built or no flow in the catalog declares it now. A check queued while a run of the
    /// flow is under way waits for it, as runs of a flow do.
    /// </summary>
    private static async Task<Results<Ok<DeliveryInventoryDuplicateCheckDto>, Accepted<DeliveryInventoryDuplicateCheckDto>, ProblemHttpResult>> CheckDuplicatesAsync(
        string partition, int inventoryId, DeliveryInventoryDuplicatesRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IRunDispatcher dispatcher, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var (key, invalid) = InventoryDuplicateKey.Of(request?.Key?.Select(k => (k?.Path, k?.Compare)).ToList());
        if (key is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid key", invalid!);
        }

        if (inventory.LastBuildRunId is null)
        {
            return Problem(StatusCodes.Status409Conflict, "Not built",
                $"Inventory '{inventory.Name}' has not been built in '{inventory.Partition}', so it holds no id to check yet; build it first.");
        }

        var (pipeline, flow, declared) = await DeclaringFlowAsync(db, documents, inventory, ct).ConfigureAwait(false);
        if (pipeline is null || flow is null || declared != true)
        {
            return Problem(StatusCodes.Status409Conflict, "Not declared",
                $"No flow in the catalog declares inventory '{inventory.Name}' of '{inventory.FlowName}' now, so no check can run through it.");
        }

        var hash = key.Hash();
        if (request!.Refresh != true
            && await ledger.FindInventoryDuplicateCheckAsync(inventory.Partition, inventory.InventoryId, hash, inventory.LastBuildRunId, ct).ConfigureAwait(false) is { } made)
        {
            var settled = await SettleCheckAsync(db, ledger, inventory.Partition, made, clock, ct).ConfigureAwait(false);
            if (settled.Status != InventoryDuplicates.Failed)
            {
                return TypedResults.Ok(await ToDtoAsync(ledger, inventory, settled, ct).ConfigureAwait(false));
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await ledger.SweepInventoryDuplicateChecksAsync(inventory.Partition, now - InventoryDuplicates.KeptFor, ct).ConfigureAwait(false);
        var checkId = await ledger.CreateInventoryDuplicateCheckAsync(
            inventory.Partition, new InventoryDuplicateCheckStart(inventory.InventoryId, RequestActor.Label(user), key.ToJson(), hash, inventory.LastBuildRunId, now),
            ct).ConfigureAwait(false);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.Partitioned)
        {
            values[PartitionNames.RunValue] = inventory.Partition;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Duplicates,
            Values = values,
            Payload = new DeliveryRunPayload { Inventories = [inventory.Name], DuplicateCheckId = checkId }.ToJson(),
        };
        Guid runId;
        try
        {
            runId = await dispatcher.EnqueueAsync(
                db,
                new RunEnqueueRequest(
                    pipeline.RepoId, pipeline.Name, pipeline.Kind, string.IsNullOrWhiteSpace(request.Pool) ? null : request.Pool.Trim(), null, parameters,
                    RequestedBy: RequestActor.Of(user)),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A check whose run never came is closed, so the page says why rather than wait on it.
            await ledger.FailInventoryDuplicateCheckAsync(
                inventory.Partition, checkId, $"its run could not be queued: {DeliveryEndpoints.Redacted(ex)}", clock.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ledger.SetInventoryDuplicateCheckRunAsync(inventory.Partition, checkId, runId, ct).ConfigureAwait(false);
        var created = await ledger.GetInventoryDuplicateCheckAsync(inventory.Partition, checkId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Duplicate check {checkId} of inventory '{inventory.Name}' was opened and is not in partition '{inventory.Partition}'.");
        return TypedResults.Accepted(
            $"/api/v1/delivery/inventories/{inventory.Partition}/{inventory.InventoryId}/duplicates/{checkId}",
            await ToDtoAsync(ledger, inventory, created, ct).ConfigureAwait(false));
    }

    /// <summary>An inventory's duplicate checks, the newest first, each as it stands.</summary>
    private static async Task<Results<JsonHttpResult<IReadOnlyList<DeliveryInventoryDuplicateCheckDto>>, ProblemHttpResult>> ListDuplicateChecksAsync(
        string partition, int inventoryId, int? limit, CatalogDbContext db, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        if (limit is < 1 or > MaxChecks)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is between 1 and {MaxChecks}.");
        }

        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var checks = await ledger.ListInventoryDuplicateChecksAsync(inventory.Partition, inventory.InventoryId, limit ?? DefaultChecks, ct).ConfigureAwait(false);
        var answered = new List<DeliveryInventoryDuplicateCheckDto>(checks.Count);
        foreach (var check in checks)
        {
            answered.Add(await ToDtoAsync(ledger, inventory, await SettleCheckAsync(db, ledger, inventory.Partition, check, clock, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
        }

        return TypedResults.Json<IReadOnlyList<DeliveryInventoryDuplicateCheckDto>>(answered, Json);
    }

    /// <summary>
    /// One duplicate check of an inventory: where it stands and what it found, its groups counted by outcome once it completed. A
    /// check whose run ended before it did is closed as failed here, saying how its run ended.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryDuplicateCheckDto>, ProblemHttpResult>> GetDuplicateCheckAsync(
        string partition, int inventoryId, long checkId, CatalogDbContext db, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (inventory, check, problem) = await CheckOfAsync(ledger, partition, inventoryId, checkId, ct).ConfigureAwait(false);
        if (check is null)
        {
            return problem!;
        }

        var settled = await SettleCheckAsync(db, ledger, inventory!.Partition, check, clock, ct).ConfigureAwait(false);
        return TypedResults.Json(await ToDtoAsync(ledger, inventory, settled, ct).ConfigureAwait(false), Json);
    }

    /// <summary>
    /// A page of a check's groups in order, the largest first, of one outcome (<c>review</c>, <c>picked</c>, <c>chosen</c>,
    /// <c>settled</c> or <c>pending</c>) or every one, after the group <c>after</c> names; each with its copies, every one beside
    /// the inventory's row of it now, when it holds few enough to list with it.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryDuplicateGroupPageDto>, ProblemHttpResult>> ListDuplicateGroupsAsync(
        string partition, int inventoryId, long checkId, string? outcome, int? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (limit is < 1 or > MaxGroups)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is between 1 and {MaxGroups}.");
        }

        var named = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim().ToLowerInvariant();
        if (named is not null && !InventoryDuplicates.Outcomes.Contains(named, StringComparer.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown outcome",
                $"'{outcome}' is not how a group of duplicates comes out; a group is {string.Join(", ", InventoryDuplicates.Outcomes)}.");
        }

        var (inventory, check, problem) = await CheckOfAsync(ledger, partition, inventoryId, checkId, ct).ConfigureAwait(false);
        if (check is null)
        {
            return problem!;
        }

        var size = limit ?? DefaultGroups;
        var page = await ledger.ListInventoryDuplicateGroupsAsync(inventory!.Partition, checkId, named, after, size, InventoryDuplicates.MaxCopiesPicked, ct).ConfigureAwait(false);
        var ledgers = await LedgerNamesAsync(ledger, page.SelectMany(g => g.Members).Select(m => m.Record).ToList(), ct).ConfigureAwait(false);
        var groups = page.Select(g => ToDto(g.Group, g.Members, ledgers)).ToList();
        return TypedResults.Json(new DeliveryDuplicateGroupPageDto(groups, groups.Count == size ? groups[^1].GroupNumber : null), Json);
    }

    /// <summary>A page of one group's copies in the inventory's order, after the row <c>after</c> names: how a group too large to list with its page is read.</summary>
    private static async Task<Results<JsonHttpResult<DeliveryDuplicateCopyPageDto>, ProblemHttpResult>> ListDuplicateCopiesAsync(
        string partition, int inventoryId, long checkId, int groupNumber, long? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (limit is < 1 or > MaxPage)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is between 1 and {MaxPage}.");
        }

        var (inventory, check, problem) = await CheckOfAsync(ledger, partition, inventoryId, checkId, ct).ConfigureAwait(false);
        if (check is null)
        {
            return problem!;
        }

        if (await ledger.GetInventoryDuplicateGroupAsync(inventory!.Partition, checkId, groupNumber, ct).ConfigureAwait(false) is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Duplicate check {checkId} holds no group {groupNumber}.");
        }

        var size = limit ?? DefaultPage;
        var members = await ledger.ListInventoryDuplicateMembersAsync(inventory.Partition, checkId, groupNumber, after, size, ct).ConfigureAwait(false);
        var ledgers = await LedgerNamesAsync(ledger, members.Select(m => m.Record).ToList(), ct).ConfigureAwait(false);
        var copies = members.Select(m => ToDto(m, ledgers)).ToList();
        return TypedResults.Json(new DeliveryDuplicateCopyPageDto(copies, copies.Count == size ? copies[^1].Record.InventoryRecordId : null), Json);
    }

    /// <summary>
    /// An operator's choice of the copy a group keeps, as the caller: the group judged again with that copy kept (the others
    /// picked to remove where the flow may remove them, and left with why otherwise), recorded with who chose and when; or, with
    /// no copy named, a choice taken back, the group judged by the check's own rules again. Refused with 409 for a check that has
    /// not completed, one the inventory was built again since, a group whose copies the check did not read, or a flow the catalog
    /// no longer declares; with 400 for a copy that is not one of the group's, or one storage no longer holds.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryDuplicateGroupDto>, ProblemHttpResult>> KeepDuplicateAsync(
        string partition, int inventoryId, long checkId, int groupNumber, DeliveryDuplicateKeepRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents,
        ILedger ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        var (inventory, check, problem) = await CheckOfAsync(ledger, partition, inventoryId, checkId, ct).ConfigureAwait(false);
        if (check is null)
        {
            return problem!;
        }

        if (DecidedProblem(inventory!, check) is { } undecidable)
        {
            return undecidable;
        }

        var group = await ledger.GetInventoryDuplicateGroupAsync(inventory!.Partition, checkId, groupNumber, ct).ConfigureAwait(false);
        if (group is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Duplicate check {checkId} holds no group {groupNumber}.");
        }

        if (group.Outcome == InventoryDuplicates.Pending || group.Copies > InventoryDuplicates.MaxCopiesPicked)
        {
            return Problem(StatusCodes.Status409Conflict, "Copies not read",
                $"Group {groupNumber} holds {group.Copies} copies, and the check read none of them: a group of more than {InventoryDuplicates.MaxCopiesPicked} is too broad to pick from. Add properties to the key and check again.");
        }

        var (_, flow, declared) = await DeclaringFlowAsync(db, documents, inventory, ct).ConfigureAwait(false);
        if (flow is null || declared != true)
        {
            return Problem(StatusCodes.Status409Conflict, "Not declared",
                $"No flow in the catalog declares inventory '{inventory.Name}' of '{inventory.FlowName}' now, so what it may remove is not known.");
        }

        var members = await ledger.ListInventoryDuplicateMembersAsync(inventory.Partition, checkId, groupNumber, null, InventoryDuplicates.MaxCopiesPicked, ct).ConfigureAwait(false);
        var chosen = request?.InventoryRecordId;
        if (chosen is { } keep)
        {
            var copy = members.FirstOrDefault(m => m.Record.InventoryRecordId == keep);
            if (copy is null)
            {
                return Problem(StatusCodes.Status400BadRequest, "Not a copy", $"Inventory row {keep} is not one of the copies of group {groupNumber}.");
            }

            if (copy.StorageState != InventoryDuplicates.Held)
            {
                return Problem(StatusCodes.Status400BadRequest, "Not held",
                    $"{copy.Record.TargetId} is not held by storage (it was {copy.StorageState ?? "not read"} when the check read the group), so it cannot be the copy kept.");
            }
        }

        var removable = flow.Removal is { } policy
            ? policy.Findings.Where(f => InventoryRemovals.Removable.Contains(f, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var copies = members
            .Select(m => new DuplicateCopy(
                m.Record.InventoryRecordId, m.Record.TargetId, m.Record.GoneUtc is null ? m.Record.Finding : InventoryFindings.Gone,
                m.StorageState ?? InventoryDuplicates.Unanswered, m.StoredVersion, m.Attributes, m.MentionedBy))
            .ToList();
        var judgement = InventoryDuplicatePicker.Judge(groupNumber, copies, removable, chosen);
        try
        {
            await ledger.WriteInventoryDuplicateChoiceAsync(
                inventory.Partition, checkId, judgement, chosen is null ? DuplicateChoice.Clear : DuplicateChoice.Choose, RequestActor.Label(user),
                clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Not written", ex.Message);
        }

        var written = await ledger.ListInventoryDuplicateGroupsAsync(inventory.Partition, checkId, null, groupNumber - 1, 1, InventoryDuplicates.MaxCopiesPicked, ct).ConfigureAwait(false);
        var page = written.FirstOrDefault(g => g.Group.GroupNumber == groupNumber)
            ?? throw new DeliveryException($"Group {groupNumber} of duplicate check {checkId} was written and is not in partition '{inventory.Partition}'.");
        var ledgers = await LedgerNamesAsync(ledger, page.Members.Select(m => m.Record).ToList(), ct).ConfigureAwait(false);
        return TypedResults.Json(ToDto(page.Group, page.Members, ledgers), Json);
    }

    /// <summary>
    /// The copies a completed check picked to remove, made a selection the inventory's removal takes, as the caller: the selection
    /// made of them already when no choice was written since (200), else a new one (201). Refused with 409 for a check that has
    /// not completed, one the inventory was built again since, or one that picked no copy to remove.
    /// </summary>
    private static async Task<Results<Ok<DeliveryInventorySelectionDto>, Created<DeliveryInventorySelectionDto>, ProblemHttpResult>> SelectDuplicatesAsync(
        string partition, int inventoryId, long checkId, ILedger ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        var (inventory, check, problem) = await CheckOfAsync(ledger, partition, inventoryId, checkId, ct).ConfigureAwait(false);
        if (check is null)
        {
            return problem!;
        }

        if (DecidedProblem(inventory!, check) is { } undecidable)
        {
            return undecidable;
        }

        if (check.ToRemove == 0)
        {
            return Problem(StatusCodes.Status409Conflict, "Nothing to remove",
                $"Duplicate check {checkId} picked no copy to remove: every group keeps its copies, is left for review, or holds copies the flow may not remove. Choose the copy to keep in the groups for review first.");
        }

        if (await CurrentSelectionAsync(ledger, inventory!, check, ct).ConfigureAwait(false) is { } current)
        {
            return TypedResults.Ok(await ToDtoAsync(ledger, inventory!, current, ct).ConfigureAwait(false));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await ledger.SweepInventorySelectionsAsync(inventory!.Partition, now - InventorySelections.KeptFor, ct).ConfigureAwait(false);
        var key = InventoryDuplicateKey.Parse(check.KeyJson);
        long selectionId;
        try
        {
            selectionId = await ledger.CreateInventoryDuplicateSelectionAsync(
                inventory.Partition, checkId, RequestActor.Label(user), new ExplorerSearch { Kind = inventory.Kind }.ToJson(),
                InventoryDuplicateColumns.Of(key.Paths, ExplorerSearch.MaxColumns), now, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Not made", ex.Message);
        }

        var made = await ledger.GetInventorySelectionAsync(inventory.Partition, selectionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Selection {selectionId} of inventory '{inventory.Name}' was made and is not in partition '{inventory.Partition}'.");
        return TypedResults.Created(
            $"/api/v1/delivery/inventories/{inventory.Partition}/{inventory.InventoryId}/selections/{selectionId}",
            await ToDtoAsync(ledger, inventory, made, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Every copy of every group of a check as CSV, written a page of groups at a time as it is read: the group, how it came out
    /// and why, the copy kept, and each copy's id, finding, versions, how many values it holds, how many records name it, its
    /// role and why, and its values at each property of the key.
    /// </summary>
    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportDuplicatesAsync(
        string partition, int inventoryId, long checkId, ILedger ledger, CancellationToken ct)
    {
        var (inventory, check, problem) = await CheckOfAsync(ledger, partition, inventoryId, checkId, ct).ConfigureAwait(false);
        if (check is null)
        {
            return problem!;
        }

        var key = InventoryDuplicateKey.Parse(check.KeyJson);
        return TypedResults.Stream(
            stream => InventoryDuplicateExport.WriteAsync(ledger, inventory!, checkId, key.Paths, stream, ct),
            InventoryExport.MediaType,
            InventoryDuplicateExport.FileName(inventory!, checkId));
    }

    /// <summary>The check a route names, when it is one of the inventory's; else the problem to answer with.</summary>
    private static async Task<(InventoryState? Inventory, InventoryDuplicateCheckState? Check, ProblemHttpResult? Problem)> CheckOfAsync(
        ILedger ledger, string partition, int inventoryId, long checkId, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return (null, null, problem);
        }

        var check = checkId < 1 ? null : await ledger.GetInventoryDuplicateCheckAsync(inventory.Partition, checkId, ct).ConfigureAwait(false);
        return check is null || check.InventoryId != inventory.InventoryId
            ? (inventory, null, Problem(StatusCodes.Status404NotFound, "Not found", $"No duplicate check {checkId} of inventory '{inventory.Name}' in partition '{inventory.Partition}'."))
            : (inventory, check, null);
    }

    /// <summary>Why a check's picks cannot be acted on now: it has not completed, or the inventory was built again since; null when they can.</summary>
    private static ProblemHttpResult? DecidedProblem(InventoryState inventory, InventoryDuplicateCheckState check)
    {
        if (check.Status != InventoryDuplicates.Completed)
        {
            return Problem(StatusCodes.Status409Conflict, "Check not completed",
                $"Duplicate check {check.InventoryDuplicateCheckId} of inventory '{inventory.Name}' is {check.Status}: only a check that read OSDU whole says which ids are copies of one another.");
        }

        return Stale(inventory, check)
            ? Problem(StatusCodes.Status409Conflict, "Check stale",
                $"Inventory '{inventory.Name}' was built again since duplicate check {check.InventoryDuplicateCheckId} read it: check for duplicates again, and act on what the new check finds.")
            : null;
    }

    /// <summary>The selection last made of a check's picks, when it is still there and no choice was written since it was made; else null.</summary>
    private static async Task<InventorySelectionState?> CurrentSelectionAsync(ILedger ledger, InventoryState inventory, InventoryDuplicateCheckState check, CancellationToken ct)
    {
        if (check.SelectionId is not { } selectionId)
        {
            return null;
        }

        var selection = await ledger.GetInventorySelectionAsync(inventory.Partition, selectionId, ct).ConfigureAwait(false);
        return selection is { Status: InventorySelections.Completed } && selection.DuplicateCheckId == check.InventoryDuplicateCheckId
            && (check.ChosenUtc is not { } chosen || selection.CreatedUtc >= chosen)
            ? selection
            : null;
    }

    /// <summary>
    /// A check as it stands: one under way is closed as failed when its run ended without finishing it, when the catalog no longer
    /// holds its run, or when its run was never queued. A check that ends between the reads keeps how it ended.
    /// </summary>
    private static async Task<InventoryDuplicateCheckState> SettleCheckAsync(
        CatalogDbContext db, ILedger ledger, string partition, InventoryDuplicateCheckState check, TimeProvider clock, CancellationToken ct)
    {
        if (!InventoryDuplicates.IsUnderWay(check.Status))
        {
            return check;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        string? why = null;
        if (check.RunId is { } runId)
        {
            var run = await db.Runs.AsNoTracking()
                .Where(r => r.RunId == runId)
                .Select(r => new { r.Status, r.Error })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (run is null)
            {
                why = $"its run {runId:D} is no longer in the catalog";
            }
            else if (RunStatuses.IsTerminal(run.Status))
            {
                why = $"its run {runId:D} ended {run.Status} before the check did{(string.IsNullOrWhiteSpace(run.Error) ? string.Empty : $": {run.Error}")}";
            }
        }
        else if (now - check.CreatedUtc > QueueGrace)
        {
            why = "its run was never queued";
        }

        if (why is null)
        {
            return check;
        }

        await ledger.FailInventoryDuplicateCheckAsync(partition, check.InventoryDuplicateCheckId, why, now, ct).ConfigureAwait(false);
        return await ledger.GetInventoryDuplicateCheckAsync(partition, check.InventoryDuplicateCheckId, ct).ConfigureAwait(false) ?? check;
    }

    /// <summary>A check as the API answers with it: its groups counted by outcome once it completed, and whether its last selection still stands.</summary>
    private static async Task<DeliveryInventoryDuplicateCheckDto> ToDtoAsync(ILedger ledger, InventoryState inventory, InventoryDuplicateCheckState c, CancellationToken ct)
    {
        IReadOnlyList<DeliveryDuplicateOutcomeCountDto>? outcomes = null;
        var current = false;
        if (c.Status == InventoryDuplicates.Completed)
        {
            var counted = await ledger.InventoryDuplicateOutcomesAsync(inventory.Partition, c.InventoryDuplicateCheckId, ct).ConfigureAwait(false);
            outcomes = InventoryDuplicates.Outcomes.Select(o => new DeliveryDuplicateOutcomeCountDto(o, counted.GetValueOrDefault(o))).ToList();
            current = await CurrentSelectionAsync(ledger, inventory, c, ct).ConfigureAwait(false) is not null;
        }

        return new DeliveryInventoryDuplicateCheckDto(
            c.InventoryDuplicateCheckId, c.InventoryId, inventory.Partition, c.Status, Stale(inventory, c), c.Phase, KeyOf(c.KeyJson), ListOf(c.NotesJson),
            c.Candidates, c.Checked, c.Keyed, c.Unkeyed, c.Outside, c.Changed, c.Groups, c.Copies, c.GroupsJudged, c.Decided, c.Review, c.ToRemove, c.Requests,
            outcomes, c.SelectionId, current, Utc(c.ChosenUtc), c.RunId, c.Actor, Utc(c.CreatedUtc), Utc(c.StartedUtc), Utc(c.CompletedUtc), c.Error);
    }

    private static DeliveryDuplicateGroupDto ToDto(InventoryDuplicateGroupState g, IReadOnlyList<InventoryDuplicateMemberState> members, IReadOnlyDictionary<Guid, string> ledgers)
        => new(
            g.GroupNumber, g.Copies, ValuesOf(g.ValuesJson), g.Outcome, g.Reason, g.KeptInventoryRecordId, g.ChosenBy, Utc(g.ChosenUtc),
            g.Copies <= InventoryDuplicates.MaxCopiesPicked ? members.Select(m => ToDto(m, ledgers)).ToList() : null);

    private static DeliveryDuplicateCopyDto ToDto(InventoryDuplicateMemberState m, IReadOnlyDictionary<Guid, string> ledgers)
        => new(ToDto(m.Record, ledgers), m.IndexedVersion, ValuesOf(m.ValuesJson), m.StorageState, m.StoredVersion, m.Attributes, m.MentionedBy, m.Role, m.Reason);

    /// <summary>Whether the inventory was built again since a completed check read it: its groups then say less than a new check would.</summary>
    private static bool Stale(InventoryState inventory, InventoryDuplicateCheckState check)
        => check.Status == InventoryDuplicates.Completed && check.BuildRunId != inventory.LastBuildRunId;

    /// <summary>The key a check keeps, as the API answers with it.</summary>
    private static IReadOnlyList<DeliveryDuplicateKeyPropertyDto> KeyOf(string json)
        => InventoryDuplicateKey.Parse(json).Properties.Select(p => new DeliveryDuplicateKeyPropertyDto(p.Path, InventoryDuplicateKey.NameOf(p.Compare))).ToList();
}
