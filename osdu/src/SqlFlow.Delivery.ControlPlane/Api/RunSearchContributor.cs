using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// What a run hit of the search carries for the GUI's renderer: the run and whether it is the one the search named, rather
/// than another run of its execution; or, when no run has the id, the reason.
/// </summary>
public sealed record DeliveryRunHit(DeliveryRunLookupRunDto? Run, bool Asked, string? Reason);

/// <summary>
/// The search's <c>runs</c> category: a query holding a run id or a correlation id, however it was copied, lists that run and
/// every run of its execution (<see cref="DeliveryRunLookup"/>). Any other query names no run, and costs no read.
/// </summary>
public sealed class RunSearchContributor : ISearchContributor
{
    /// <summary>The category's key: the member the combined search carries it under, and its route segment.</summary>
    public const string CategoryKey = "runs";

    private readonly CatalogDbContext _catalog;
    private readonly ILedger _ledger;
    private readonly IEnumerable<IRunOutcomeDescriber> _describers;
    private readonly ILogger<RunSearchContributor> _log;

    public RunSearchContributor(
        CatalogDbContext catalog, ILedger ledger, IEnumerable<IRunOutcomeDescriber> describers, ILogger<RunSearchContributor> log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(describers);
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _ledger = ledger;
        _describers = describers;
        _log = log;
    }

    public string Key => CategoryKey;

    public string Label => "Runs";

    /// <summary>The search surface's own read policy is enough: a run hit carries no more than the run page does.</summary>
    public string? RequiredPolicy => null;

    public async Task<SearchContribution> SearchAsync(SearchContributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = await DeliveryRunLookup.FindAsync(request.Phrase, _catalog, _ledger, _describers, _log, ct).ConfigureAwait(false);
        if (found is null)
        {
            return new SearchContribution([], 0);
        }

        if (found.Run is null)
        {
            // An id that names no run still says so: an empty category would read as a search that never looked.
            return new SearchContribution(
                [new SearchHitDto(found.Id.ToString("D"), found.Id.ToString("D"), found.Reason, null, new DeliveryRunHit(null, false, found.Reason))],
                1);
        }

        var items = found.Execution
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(run => new SearchHitDto(
                run.RunId.ToString("D"),
                run.FlowName,
                run.Status,
                $"/runs/{run.RunId:D}",
                new DeliveryRunHit(run, run.RunId == found.Id, null)))
            .ToList();
        return new SearchContribution(items, found.Execution.Count);
    }
}
