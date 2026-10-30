using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One run of an execution: where it stands, what it did in a few words (<see cref="RunOutcomeDescriptions"/>), and, of a
/// part of a split delivery, the run that coordinates it, which part it is from 1 and how many there are.
/// </summary>
public sealed record DeliveryRunLookupRunDto(
    Guid RunId, Guid PipelineId, string FlowName, string FlowKind, string Status, string? Operation, string? RequestedBy,
    string? TriggerSource, DateTime? EnqueuedUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds, string? Error,
    string? Outcome, Guid? FanOutRoot, int? FanOutSlot, int? FanOutCount);

/// <summary>
/// What a run id or a correlation id leads to (osdu/docs/reference/concepts/request-correlation.md): the id it holds and the
/// repository's and the operation's codes it carries; the run with that id, or the reason there is none; every run of its
/// execution, the coordinating run first, then the parts; and the plans the execution registered, with their counts.
/// </summary>
public sealed record DeliveryRunLookupDto(
    Guid Id, string? Repository, string? Operation, DeliveryRunLookupRunDto? Run, IReadOnlyList<DeliveryRunLookupRunDto> Execution,
    IReadOnlyList<DeliverySubmissionDto> Submissions, string? Reason);

/// <summary>
/// A run found by what an operator holds, which is usually a value copied from a platform's log: the correlation id of the
/// requests it sent, or its bare id. A delivery split over nodes sends under one id per part, so any of them leads to the whole
/// execution. The GUI's search, the MCP server's delivery_run and <c>GET /delivery/runs/lookup</c> all answer from here.
/// </summary>
public static class DeliveryRunLookup
{
    /// <summary>More runs than a delivery is ever split into: at most 64 parts (<c>parallelism.nodes</c>) and the run that coordinates them.</summary>
    private const int ExecutionRead = 200;

    /// <summary>Why an id leads to no run, for an id the text did hold.</summary>
    public const string NoRun =
        "No run has this id. A request sent outside a run (an explorer read, a probe) carries an id of its own that nothing here "
        + "records; otherwise the run belongs to another environment, or its history has been removed.";

    /// <summary>What a run lookup's argument has to hold.</summary>
    internal const string NoId = "Give a run id or a correlation id: osdu-delivery-<repository>-<operation>-<run id>.";

    internal static async Task<Results<Ok<DeliveryRunLookupDto>, ProblemHttpResult>> LookupAsync(
        string? id, CatalogDbContext catalog, ILedger ledger, IEnumerable<IRunOutcomeDescriber> describers, ILoggerFactory loggers, CancellationToken ct)
    {
        var found = await FindAsync(id, catalog, ledger, describers, loggers.CreateLogger(typeof(DeliveryRunLookup)), ct).ConfigureAwait(false);
        return found is null
            ? TypedResults.Problem(title: "No id", detail: NoId, statusCode: StatusCodes.Status400BadRequest)
            : TypedResults.Ok(found);
    }

    /// <summary>What <paramref name="text"/> leads to; null when it holds no id.</summary>
    public static async Task<DeliveryRunLookupDto?> FindAsync(
        string? text, CatalogDbContext catalog, ILedger ledger, IEnumerable<IRunOutcomeDescriber> describers, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(describers);
        ArgumentNullException.ThrowIfNull(log);
        if (OsduCorrelation.Find(text) is not { } value)
        {
            return null;
        }

        var run = await catalog.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == value.Id, ct).ConfigureAwait(false);
        if (run is null)
        {
            return new DeliveryRunLookupDto(value.Id, value.Repository, value.Operation, null, [], [], NoRun);
        }

        // The run that coordinates a split delivery, whichever part was asked for: the root is the first row, then the parts by slot.
        var root = run.FanOutRoot ?? run.RunId;
        var execution = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == root || r.FanOutRoot == root)
            .OrderBy(r => r.FanOutRoot != null)
            .ThenBy(r => r.FanOutSlot)
            .ThenBy(r => r.RunId)
            .Take(ExecutionRead)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var described = describers as IReadOnlyCollection<IRunOutcomeDescriber> ?? describers.ToList();
        var submissions = await ledger.ListSubmissionsOfRunAsync(root, ct).ConfigureAwait(false);
        return new DeliveryRunLookupDto(
            value.Id,
            value.Repository,
            value.Operation,
            Row(run, described, log),
            execution.Select(r => Row(r, described, log)).ToList(),
            submissions.Select(DeliveryEndpoints.ToDto).ToList(),
            null);
    }

    private static DeliveryRunLookupRunDto Row(CatalogRun run, IEnumerable<IRunOutcomeDescriber> describers, ILogger log) => new(
        run.RunId, run.PipelineId, run.FlowName, run.FlowKind, run.Status, run.Operation, run.RequestedBy, run.TriggerSource,
        run.EnqueuedUtc, run.StartUtc, run.EndUtc, run.DurationSeconds, run.Error,
        RunOutcomeDescriptions.Describe(run, describers, log), run.FanOutRoot, run.FanOutSlot, run.FanOutCount);
}
