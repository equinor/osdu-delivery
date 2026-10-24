using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A match an operator asks of an inventory's ids (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds):
/// the conditions, as the explorer writes them (a property, how the platform indexes it, and what it must hold; or a search
/// term, a column of a source system), whether to read OSDU again rather than take the match already made of the same
/// conditions against the same build (<c>refresh</c>), and the pool its run is queued on.
/// </summary>
public sealed record DeliveryInventorySelectionRequest(IReadOnlyList<DeliveryExplorerFilterDto>? Filters, bool? Refresh = null, string? Pool = null);

/// <summary>
/// One selection of an inventory's ids: its conditions as the operator wrote them, where its match stands (queued, running,
/// completed, failed or expired) and whether the inventory was built again since (<c>Stale</c>), the query the conditions came
/// to and how the match read OSDU, what it had to say, the properties its rows carry, how far it got and what it found (the
/// inventory's ids that hold what the conditions ask, records outside the inventory, ids matched at another version), the
/// search requests it sent, how many of its ids have each finding now (<c>Counts</c>, once it completed), its run, who
/// asked and when, and why it failed. A selection a duplicate check made of its picks names the check and its key
/// (<c>DuplicateCheckId</c>, <c>DuplicateKey</c>) and holds no conditions.
/// </summary>
public sealed record DeliveryInventorySelectionDto(
    long SelectionId, int InventoryId, string Partition, string Status, bool Stale, IReadOnlyList<DeliveryExplorerFilterDto> Conditions, string? Query,
    string? Direction, IReadOnlyList<string> Notes, IReadOnlyList<string> Columns, long Candidates, long ToCheck, long Checked, long Matched, long Outside,
    long Changed, long Requests, IReadOnlyList<DeliveryInventoryCountDto>? Counts, Guid? RunId, string Actor, DateTime CreatedUtc, DateTime? StartedUtc,
    DateTime? CompletedUtc, string? Error, long? DuplicateCheckId = null, IReadOnlyList<DeliveryDuplicateKeyPropertyDto>? DuplicateKey = null);

/// <summary>
/// The selection a removal took its ids from, as the removal names it: its number, where it stands, its conditions and query,
/// what it matched and when; or the duplicate check whose picks it holds, with the check's key.
/// </summary>
public sealed record DeliveryInventorySelectionSummaryDto(
    long SelectionId, string Status, IReadOnlyList<DeliveryExplorerFilterDto> Conditions, string? Query, long Matched, DateTime? MatchedUtc,
    long? DuplicateCheckId = null, IReadOnlyList<DeliveryDuplicateKeyPropertyDto>? DuplicateKey = null);

public static partial class DeliveryInventoryEndpoints
{
    /// <summary>How long a selection waits for its run to be named before it is taken as one whose run was never queued.</summary>
    private static readonly TimeSpan QueueGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Queues the match of an inventory's ids against an operator's conditions, as the caller
    /// (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds): each condition naming a search term is carried
    /// through its mapping, every condition is checked as the explorer checks it, and a selection is opened and filled by a
    /// match run of the inventory flow, through its own source. The same conditions asked again of the same build answer with
    /// the match made of them already (200), unless <c>refresh</c> asks for OSDU to be read again; a new match answers 202.
    /// Refused with 400 for conditions that cannot be asked, and with 409 when the inventory was never built or no flow in the
    /// catalog declares it now. A match queued while a run of the flow is under way waits for it, as runs of a flow do.
    /// </summary>
    private static async Task<Results<Ok<DeliveryInventorySelectionDto>, Accepted<DeliveryInventorySelectionDto>, ProblemHttpResult>> SelectAsync(
        string partition, int inventoryId, DeliveryInventorySelectionRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents,
        ILedger ledger, IRunDispatcher dispatcher, TimeProvider clock, IRepositoryFolders folders, ClaimsPrincipal user, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var conditions = request?.Filters;
        if (conditions is null || conditions.Count == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Conditions required",
                "Name at least one condition (filters): the property, how the platform indexes it, and what it must hold. Every id of a finding is on its own tab already.");
        }

        if (conditions.Count > ExplorerSearch.MaxFilters)
        {
            return Problem(StatusCodes.Status400BadRequest, "Too many conditions",
                $"A selection narrows to at most {ExplorerSearch.MaxFilters} conditions at once; {conditions.Count} were asked.");
        }

        if (conditions.Any(c => c is null))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid condition", "Every condition names its property, how the platform indexes it, and what it must hold.");
        }

        if (inventory.LastBuildRunId is null)
        {
            return Problem(StatusCodes.Status409Conflict, "Not built",
                $"Inventory '{inventory.Name}' has not been built in '{inventory.Partition}', so it holds no id to match yet; build it first.");
        }

        var (pipeline, flow, declared) = await DeclaringFlowAsync(db, documents, inventory, ct).ConfigureAwait(false);
        if (pipeline is null || flow is null || declared != true)
        {
            return Problem(StatusCodes.Status409Conflict, "Not declared",
                $"No flow in the catalog declares inventory '{inventory.Name}' of '{inventory.FlowName}' now, so no match can run through it.");
        }

        // The conditions as the explorer would ask them of the inventory's kind: a term carried through its mapping in the
        // inventory's partition, every condition checked as a page of the explorer checks it.
        var directory = new SearchTermDirectory(osdu, documents, clock, folders);
        var (terms, unresolved) = await DeliveryExplorerEndpoints.ResolveTermsAsync(conditions, inventory.Partition, inventory.Kind, directory, ct).ConfigureAwait(false);
        if (unresolved is not null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid condition", unresolved);
        }

        var (search, invalid) = DeliveryExplorerEndpoints.SearchOf(new DeliveryExplorerSearchRequest(Kind: inventory.Kind, Filters: conditions), terms);
        if (search is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid condition", invalid!);
        }

        // The conditions as the operator wrote them, in one spelling, so the same conditions asked again find the match made of them.
        var conditionsJson = JsonSerializer.Serialize(conditions.Select(c => Canonical(c!)).ToList(), Json);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conditionsJson)));
        if (request!.Refresh != true
            && await ledger.FindInventorySelectionAsync(inventory.Partition, inventory.InventoryId, key, inventory.LastBuildRunId, ct).ConfigureAwait(false) is { } made)
        {
            var settled = await SettleAsync(db, ledger, inventory.Partition, made, clock, ct).ConfigureAwait(false);
            if (settled.Status != InventorySelections.Failed)
            {
                return TypedResults.Ok(await ToDtoAsync(ledger, inventory, settled, ct).ConfigureAwait(false));
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await ledger.SweepInventorySelectionsAsync(inventory.Partition, now - InventorySelections.KeptFor, ct).ConfigureAwait(false);
        var selectionId = await ledger.CreateInventorySelectionAsync(
            inventory.Partition,
            new InventorySelectionStart(inventory.InventoryId, RequestActor.Label(user), conditionsJson, key, search.ToJson(), inventory.LastBuildRunId, now),
            ct).ConfigureAwait(false);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.Partitioned)
        {
            values[PartitionNames.RunValue] = inventory.Partition;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Match,
            Values = values,
            Payload = new DeliveryRunPayload { Inventories = [inventory.Name], SelectionId = selectionId }.ToJson(),
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
            // A selection whose run never came is closed, so the page says why rather than wait on it.
            await ledger.FailInventorySelectionAsync(
                inventory.Partition, selectionId, $"its run could not be queued: {DeliveryEndpoints.Redacted(ex)}", clock.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ledger.SetInventorySelectionRunAsync(inventory.Partition, selectionId, runId, ct).ConfigureAwait(false);
        var created = await ledger.GetInventorySelectionAsync(inventory.Partition, selectionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Selection {selectionId} of inventory '{inventory.Name}' was opened and is not in partition '{inventory.Partition}'.");
        return TypedResults.Accepted(
            $"/api/v1/delivery/inventories/{inventory.Partition}/{inventory.InventoryId}/selections/{selectionId}",
            await ToDtoAsync(ledger, inventory, created, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// One selection of an inventory: where its match stands and what it found, its ids counted by the finding the inventory gives
    /// each now. A selection whose run ended before its match did is closed as failed here, saying how its run ended.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventorySelectionDto>, ProblemHttpResult>> GetSelectionAsync(
        string partition, int inventoryId, long selectionId, CatalogDbContext db, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var (selection, missing) = await SelectionAsync(ledger, inventory, selectionId, ct).ConfigureAwait(false);
        if (selection is null)
        {
            return missing!;
        }

        var settled = await SettleAsync(db, ledger, inventory.Partition, selection, clock, ct).ConfigureAwait(false);
        return TypedResults.Json(await ToDtoAsync(ledger, inventory, settled, ct).ConfigureAwait(false), Json);
    }

    /// <summary>The selection a route names, when it is one of the inventory's; else the problem to answer with.</summary>
    private static async Task<(InventorySelectionState? Selection, ProblemHttpResult? Problem)> SelectionAsync(
        ILedger ledger, InventoryState inventory, long selectionId, CancellationToken ct)
    {
        var selection = selectionId < 1 ? null : await ledger.GetInventorySelectionAsync(inventory.Partition, selectionId, ct).ConfigureAwait(false);
        return selection is null || selection.InventoryId != inventory.InventoryId
            ? (null, Problem(StatusCodes.Status404NotFound, "Not found", $"No selection {selectionId} of inventory '{inventory.Name}' in partition '{inventory.Partition}'."))
            : (selection, null);
    }

    /// <summary>
    /// A selection as it stands: one whose match is under way is closed as failed when its run ended without finishing it (the
    /// process stopped, or the run was cancelled), when the catalog no longer holds its run, or when its run was never queued.
    /// A match that ends between the reads keeps how it ended, since only a selection still under way is closed.
    /// </summary>
    private static async Task<InventorySelectionState> SettleAsync(
        CatalogDbContext db, ILedger ledger, string partition, InventorySelectionState selection, TimeProvider clock, CancellationToken ct)
    {
        if (!InventorySelections.IsUnderWay(selection.Status))
        {
            return selection;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        string? why = null;
        if (selection.RunId is { } runId)
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
                why = $"its run {runId:D} ended {run.Status} before the match did{(string.IsNullOrWhiteSpace(run.Error) ? string.Empty : $": {run.Error}")}";
            }
        }
        else if (now - selection.CreatedUtc > QueueGrace)
        {
            why = "its run was never queued";
        }

        if (why is null)
        {
            return selection;
        }

        await ledger.FailInventorySelectionAsync(partition, selection.InventorySelectionId, why, now, ct).ConfigureAwait(false);
        return await ledger.GetInventorySelectionAsync(partition, selection.InventorySelectionId, ct).ConfigureAwait(false) ?? selection;
    }

    /// <summary>A selection as the API answers with it: its counts by finding read from its ids now, once its match completed.</summary>
    private static async Task<DeliveryInventorySelectionDto> ToDtoAsync(ILedger ledger, InventoryState inventory, InventorySelectionState s, CancellationToken ct)
    {
        IReadOnlyList<DeliveryInventoryCountDto>? counts = null;
        if (s.Status == InventorySelections.Completed)
        {
            counts = Counts((await ledger.InventorySelectionCountsAsync(inventory.Partition, s.InventorySelectionId, ct).ConfigureAwait(false)).ByFinding);
        }

        return new DeliveryInventorySelectionDto(
            s.InventorySelectionId, s.InventoryId, inventory.Partition, s.Status, Stale(inventory, s), ConditionsOf(s.ConditionsJson), s.Query, s.Direction,
            ListOf(s.NotesJson), ListOf(s.ColumnsJson), s.Candidates, s.ToCheck, s.Checked, s.Matched, s.Outside, s.Changed, s.Requests, counts, s.RunId, s.Actor,
            Utc(s.CreatedUtc), Utc(s.StartedUtc), Utc(s.CompletedUtc), s.Error, s.DuplicateCheckId,
            await DuplicateKeyOfAsync(ledger, inventory.Partition, s, ct).ConfigureAwait(false));
    }

    /// <summary>A selection as a removal names it.</summary>
    private static DeliveryInventorySelectionSummaryDto Summary(InventorySelectionState s, IReadOnlyList<DeliveryDuplicateKeyPropertyDto>? key)
        => new(s.InventorySelectionId, s.Status, ConditionsOf(s.ConditionsJson), s.Query, s.Matched, Utc(s.CompletedUtc), s.DuplicateCheckId, key);

    /// <summary>The key of the duplicate check that made a selection, while the partition keeps the check; null for a selection a match filled.</summary>
    private static async Task<IReadOnlyList<DeliveryDuplicateKeyPropertyDto>?> DuplicateKeyOfAsync(ILedger ledger, string partition, InventorySelectionState s, CancellationToken ct)
        => s.DuplicateCheckId is { } checkId && await ledger.GetInventoryDuplicateCheckAsync(partition, checkId, ct).ConfigureAwait(false) is { } check
            ? KeyOf(check.KeyJson)
            : null;

    /// <summary>Whether the inventory was built again since a completed selection was matched: its ids then say less than a new match would.</summary>
    private static bool Stale(InventoryState inventory, InventorySelectionState selection)
        => selection.Status == InventorySelections.Completed && selection.BuildRunId != inventory.LastBuildRunId;

    /// <summary>A condition in the one spelling a selection keeps: names as the API writes them, paths trimmed, and what holds no value left out.</summary>
    private static DeliveryExplorerFilterDto Canonical(DeliveryExplorerFilterDto condition)
    {
        static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var index = Enum.TryParse<OsduFieldIndex>(Trimmed(condition.Index) ?? nameof(OsduFieldIndex.Text), ignoreCase: true, out var parsedIndex)
            ? JsonNamingPolicy.CamelCase.ConvertName(parsedIndex.ToString())
            : Trimmed(condition.Index);
        var named = Enum.TryParse<ExplorerCondition>(Trimmed(condition.Condition) ?? nameof(ExplorerCondition.Is), ignoreCase: true, out var parsedCondition)
            ? JsonNamingPolicy.CamelCase.ConvertName(parsedCondition.ToString())
            : Trimmed(condition.Condition);
        return new DeliveryExplorerFilterDto(
            Trimmed(condition.Path), index, condition.Value, named, condition.Values is { Count: > 0 } values ? values : null, condition.To, Trimmed(condition.Nested), condition.Term);
    }

    /// <summary>The conditions a selection keeps, as the API answers with them.</summary>
    private static IReadOnlyList<DeliveryExplorerFilterDto> ConditionsOf(string json)
        => JsonSerializer.Deserialize<List<DeliveryExplorerFilterDto>>(json, Json)
            ?? throw new DeliveryException("A selection's conditions are not the list of conditions a selection keeps.");

    /// <summary>A list of text a selection keeps (its notes, its columns); none when it keeps none.</summary>
    private static IReadOnlyList<string> ListOf(string? json)
        => string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
