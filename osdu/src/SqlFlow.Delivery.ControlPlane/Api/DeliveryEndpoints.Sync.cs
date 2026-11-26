using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A run was queued for a record-scoped operation (redeliver, verify).</summary>
public sealed record DeliveryRunAccepted(Guid RunId, string Status);

/// <summary>
/// Which of a flow's records a sync reads: those <c>Keys</c> names, every one <c>Filter</c> matches (resolved to keys when the
/// sync is queued, and refused when it no longer matches the <c>Expected</c> count the operator was shown), or, with neither,
/// every record of the flow.
/// </summary>
public sealed record DeliverySyncRequest(IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected);

/// <summary>
/// A flow's records checked against what is there, each as a run queued for a node: one record verified against what
/// OSDU holds, and one record, or many, synced with the ingestion tables, which renders nothing and sends nothing.
/// </summary>
public static partial class DeliveryEndpoints
{
    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> VerifyRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedgerRecords ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Verify,
            Payload = new DeliveryRunPayload { RecordKeys = [key], Force = true, Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    /// <summary>
    /// Consolidates the ledger with the ingestion tables for one record: a sync run on a node reads the record's row by the
    /// key the ledger stored, in the scope it was planned under, records what the ledger lacks of it, asks for it to be
    /// planned by the flow's next run when its row changed unseen, and puts a row that is gone on its history. It renders
    /// nothing and sends nothing to OSDU; the run and its activity say what it found.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> SyncRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedgerRecords ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Sync,
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    /// <summary>
    /// The same for records of one interface (the flow's, in the single form): the ones the request names by key, every one
    /// its filter matches, or every record of the interface. A sync of every record pages the ledger in key order and
    /// consolidates each page with the rows the ingestion tables hold. Rows the ledger has no record of are a plan's to
    /// find, since a row's delivery key comes from its mapping.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> SyncFlowAsync(
        Guid pipelineId, DeliverySyncRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var keys = new List<Guid>();
        if (request?.Keys is { Count: > 0 } named)
        {
            if (request.Filter is not null)
            {
                return TypedResults.Problem(
                    detail: "A sync names its records either by keys or by filter, not both.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            keys.AddRange(named.Distinct());
            if (keys.Count > SourceSync.MaxNamedRecords)
            {
                return TypedResults.Problem(
                    detail: $"A sync names at most {SourceSync.MaxNamedRecords} records; {keys.Count} were selected. Sync every record of the flow instead.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Too many records");
            }
        }
        else if (request?.Filter is { } filter)
        {
            var (query, invalid) = await BuildQueryAsync(db, filter, ct).ConfigureAwait(false);
            if (query is null)
            {
                return invalid!;
            }

            BoundedCount matched;
            try
            {
                matched = await ledger.CountAsync(flow.FlowId, query, SourceSync.MaxNamedRecords + 1, ct).ConfigureAwait(false);
            }
            catch (RecordQueryTooBroadException ex)
            {
                return TooBroad(ex.Message);
            }

            if (!matched.Exact || matched.Count > SourceSync.MaxNamedRecords)
            {
                return TypedResults.Problem(
                    detail: $"The filter matches more than {SourceSync.MaxNamedRecords} records as far as the listing counts; sync every record of the flow, or narrow the filter.",
                    statusCode: StatusCodes.Status409Conflict, title: "Too many records");
            }

            if (matched.Count == 0)
            {
                return TypedResults.Problem(
                    detail: "The filter matches no records, so there is nothing to sync.",
                    statusCode: StatusCodes.Status409Conflict, title: "Nothing selected");
            }

            if (request.Expected is { } expected && expected != matched.Count)
            {
                return TypedResults.Problem(
                    detail: $"The filter matched {expected} records when it was shown and matches {matched.Count} now. Nothing was queued; check the list and ask again.",
                    statusCode: StatusCodes.Status409Conflict, title: "The selection changed");
            }

            keys.AddRange((await ledger.ListKeysAsync(flow.FlowId, query, SourceSync.MaxNamedRecords, ct).ConfigureAwait(false)).Select(k => k.Value));
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Sync,
            Payload = new DeliveryRunPayload { RecordKeys = keys, Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }
}
