using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.SearchTerms;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// The conditions on search terms a search asks (osdu/docs/reference/concepts/search-terms.md): each turned into the
/// condition on the record its term's route fills, in the partition the search reads, before the search is checked and
/// run.
/// </summary>
public static partial class DeliveryExplorerEndpoints
{
    /// <summary>Every condition the explorer asks, as a message lists them.</summary>
    internal const string ConditionNames = "is, isNot, anyOf, noneOf, contains, startsWith, range, exists or missing";

    /// <summary>
    /// The conditions of <paramref name="body"/> that name a search term, each resolved by place in the partition the search
    /// reads: the one asked, else the workbench's, else the registry's default, as the search's own connection settles it.
    /// None for a search naming no term; a problem answer for a term that cannot be searched as asked, or a partition the
    /// explorer has no connection to.
    /// </summary>
    internal static async Task<(IReadOnlyDictionary<int, ExplorerFilter>? Terms, Results<ContentHttpResult, ProblemHttpResult>? Failure)> TermsOfAsync(
        DeliveryExplorerSearchRequest body, string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, HttpRequest request,
        SearchTermDirectory directory, CancellationToken ct)
    {
        var filters = body.Filters ?? [];
        if (!filters.Any(f => f?.Term is not null))
        {
            return (null, null);
        }

        var found = await ConnectAsync(partitions, config, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (found.Configured is null || found.Partition is not { } read)
        {
            return (null, NoConnection(found.Reason));
        }

        var (resolved, problem) = await ResolveTermsAsync(filters, read, body.Kind, directory, ct).ConfigureAwait(false);
        return problem is null ? (resolved, null) : (null, DeliveryEndpoints.Invalid(problem));
    }

    /// <summary>
    /// The conditions of <paramref name="filters"/> that name a search term, each resolved by place in <paramref name="partition"/>
    /// for <paramref name="kind"/>: the route of the term for the kind searched, the version of the mapping that renders it where
    /// versions differ. None for conditions naming no term; the problem, in words a reader acts on, for a term that cannot be
    /// searched as asked. The explorer resolves them in the partition it reads, an inventory's match in the inventory's own.
    /// </summary>
    internal static async Task<(IReadOnlyDictionary<int, ExplorerFilter>? Terms, string? Problem)> ResolveTermsAsync(
        IReadOnlyList<DeliveryExplorerFilterDto?> filters, string partition, string? kind, SearchTermDirectory directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(directory);
        if (!filters.Any(f => f?.Term is not null))
        {
            return (null, null);
        }

        var resolved = new Dictionary<int, ExplorerFilter>();
        for (var place = 0; place < filters.Count; place++)
        {
            if (filters[place] is not { Term: { } term } filter)
            {
                continue;
            }

            if (!TryParse<ExplorerCondition>(filter.Condition, ExplorerCondition.Is, out var condition))
            {
                return (null, $"'{filter.Condition}' is not a condition: {ConditionNames}.");
            }

            try
            {
                resolved[place] = await directory.ResolveAsync(term, new SearchTermCondition(condition, filter.Value, filter.Values, filter.To), partition, kind, ct).ConfigureAwait(false);
            }
            catch (DeliveryException ex)
            {
                return (null, ex.Message);
            }
        }

        return (resolved, null);
    }
}
