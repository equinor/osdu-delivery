using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A preview of one record of a flow: the record's key, or none for the scope's first record, the flow parameter values the
/// scope is read with (the declared defaults fill what is not given), and whether to hold the document to the partition's
/// Schema service as well.
/// </summary>
public sealed record DeliveryPreviewRequest(
    string? Key,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values,
    bool? SchemaCheck = null);

/// <summary>
/// A read of one OSDU record through a flow's route and credentials, by its id (a version or a trailing colon is dropped),
/// at its latest version or at the one <c>Version</c> names. A record's own read names no id: the ledger's record says which.
/// </summary>
public sealed record DeliveryReadRequest(string? TargetId, long? Version = null);

/// <summary>
/// What a person waits on, run in this process under the flow's own credentials: a record read back from OSDU, by its
/// ledger record or by an OSDU id, its rows as the ingestion tables hold them, a preview of a flow's record or of one
/// record as it would render now, and the values a scope can take (<see cref="DirectOperationRunner"/>); and a probe
/// of the flow's target, which the audit trail records (<see cref="TargetProbes"/>).
/// </summary>
public static partial class DeliveryEndpoints
{
    /// <summary>
    /// The record's rows as the ingestion tables hold them now (osdu/docs/reference/concepts/api.md, Reading OSDU and the
    /// source), read in this process (<see cref="DirectOperationRunner"/>), which opens the flow's source with the flow's
    /// own connection reference and reads the record by the key tuple the ledger stored. Nothing is planned and nothing is
    /// delivered; the answer carries the record row with its system columns, its child datasets and the origin file and row
    /// the ingestion tables record.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadSourceAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["deliveryKey"] = key.ToString("D") };
        if (record.LastSubmissionId is { } lastId
            && await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) is { ParametersJson.Length: > 2 } last)
        {
            // The scope predicate of the read is the one the record was planned under, so a flow whose scope is a
            // parameter reads the row in the record's own scope rather than in the flow's declared defaults.
            arguments["values"] = last.ParametersJson;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ReadSourceRowOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadRecordAsync(
        Guid flowId, Guid key, DeliveryReadRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedgerRecords ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        if (InvalidVersion(request) is { } invalid)
        {
            return invalid;
        }

        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["deliveryKey"] = key.ToString("D") };
        WithVersion(arguments, request);
        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ReadRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>A version to read at is a positive whole number; the storage service numbers versions from one.</summary>
    internal static ProblemHttpResult? InvalidVersion(DeliveryReadRequest? request)
        => request?.Version is { } version && version <= 0 ? Invalid($"{version.ToString(CultureInfo.InvariantCulture)} is not a record version: a positive whole number.") : null;

    internal static void WithVersion(Dictionary<string, string> arguments, DeliveryReadRequest? request)
    {
        if (request?.Version is { } version)
        {
            arguments["version"] = version.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// One record of a flow rendered in this process (<see cref="DirectOperationRunner"/>) as a delivery would render it, with
    /// nothing sent: the scope's first record, or the one the request's key names. The key and the parameter values are
    /// checked before the preview runs, so a request the preview would refuse is a 400 rather than a preview that fails;
    /// whether the key names a row is the preview's to find, in the flow's own ingestion tables, and is an answer rather
    /// than a failure.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> PreviewAsync(
        Guid pipelineId, DeliveryPreviewRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request?.Key?.Trim() is { Length: > 0 } key)
        {
            if (key.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return Invalid($"The key is {key.Length} characters long; a key a preview reads is at most {ComputeTaskPayload.MaxArgumentLength}.");
            }

            if (key.Any(char.IsControl))
            {
                return Invalid("The key holds a control character (a tab, a line break); name one record, on one line.");
            }

            arguments["key"] = key;
        }

        var values = request?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (ParameterProblem(flow.Flow, values) is { } refused)
        {
            return Invalid(refused);
        }

        if (values.Count > 0)
        {
            var json = RunParameters.ValuesToJson(values)!;
            if (json.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return Invalid($"The parameter values are {json.Length} characters as JSON; a preview carries at most {ComputeTaskPayload.MaxArgumentLength}.");
            }

            arguments["values"] = json;
        }

        if (request?.SchemaCheck == true)
        {
            arguments[PreviewRecordOperation.SchemaCheckArgument] = "true";
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, PreviewRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The values each parameter of an interface's scope predicate can take, read in this process
    /// (<see cref="DirectOperationRunner"/>) from the column <c>source.record.scope</c> binds it to in the flow's own record
    /// table: what a page offers for a scope's value rather than having it typed. Nothing is written.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ScopeValuesAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ScopeValuesOperation.OperationName, new Dictionary<string, string>(StringComparer.Ordinal), user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the parameter values cannot read the flow's scope, or null when they can, by the rule a run's values follow
    /// (<see cref="FlowParameters.RunInputs"/>): a name the flow does not declare, a required parameter without a default
    /// that is given no value (an empty one is none), or a value the parameter cannot take.
    /// </summary>
    internal static string? ParameterProblem(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var given = values
            .Where(v => !(flow.Parameters.ContainsKey(v.Key) && string.IsNullOrWhiteSpace(v.Value)))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var explained = RunValueResolution.Explain(FlowParameters.RunInputs(flow.Parameters, flow.Source.Record.Scope.Values), given);

        var undeclared = explained.Where(v => v.Origin == RunValueOrigin.NotDeclared).Select(v => v.Name).ToList();
        if (undeclared.Count > 0)
        {
            var declared = flow.Parameters.Count == 0 ? "it declares none" : $"it declares {string.Join(", ", flow.Parameters.Keys)}";
            return $"The flow declares no parameter {string.Join(", ", undeclared.Select(n => $"'{n}'"))}; {declared}.";
        }

        var missing = explained.Where(v => v.Origin == RunValueOrigin.Missing).Select(v => v.Name).ToList();
        if (missing.Count > 0)
        {
            return $"The flow's scope needs a value for {string.Join(", ", missing)}, which it declares required and gives no default.";
        }

        return explained.FirstOrDefault(v => v.Problem is not null)?.Problem is { } refused ? $"The flow's {refused}" : null;
    }

    /// <summary>
    /// The record a record page shows, rendered in this process (<see cref="DirectOperationRunner"/>) from its current source
    /// row as a delivery would render it now, with nothing sent: what the page compares with what OSDU holds. The row is read
    /// in the scope the record was last planned under, as the source row read reads it.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> PreviewRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = key.ToString("D") };
        if (record.LastSubmissionId is { } lastId
            && await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) is { ParametersJson.Length: > 2 } last)
        {
            if (last.ParametersJson.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return TypedResults.Problem(
                    detail: $"The scope the record was last planned under is {last.ParametersJson.Length} characters as JSON, more than the {ComputeTaskPayload.MaxArgumentLength} an operation's argument carries; preview it from the flow's Preview tab with its values.",
                    statusCode: StatusCodes.Status409Conflict, title: "Scope too large to preview");
            }

            arguments["values"] = last.ParametersJson;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, PreviewRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>The longest OSDU id a read takes; storage ids are far shorter, and a longer text is not one.</summary>
    private const int MaxTargetIdLength = 1024;

    /// <summary>
    /// One OSDU record read in this process (<see cref="DirectOperationRunner"/>) through a flow's route and credentials, by
    /// its id: a record a document refers to, which the ledger may never have delivered. The id may carry a version or the
    /// trailing colon of a reference; the read is of the record, at its latest version. Nothing is written.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadTargetAsync(
        Guid pipelineId, DeliveryReadRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        if (TargetProblem(request, out var asked) is { } invalid)
        {
            return invalid;
        }

        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["targetId"] = TargetId.WithoutVersion(asked) };
        WithVersion(arguments, request);
        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ReadRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why a read by OSDU id cannot be asked (no id, one too long, text that is no record id, a version that is no version),
    /// or null with the id as asked in <paramref name="asked"/>. Every read of a record by its id checks it this way: a
    /// delivery's records by the id a delivery writes (<c>partition:group--Type:unique</c>), and with
    /// <paramref name="anyStorageId"/> (the explorer's reads of what OSDU holds) by any id the storage service takes, a
    /// partition's own kinds naming types of no group among them (<see cref="TargetId.IsStorageId"/>).
    /// </summary>
    internal static ProblemHttpResult? TargetProblem(DeliveryReadRequest? request, out string asked, bool anyStorageId = false)
    {
        asked = request?.TargetId?.Trim() ?? string.Empty;
        if (asked.Length == 0)
        {
            return Invalid("Name the OSDU id to read.");
        }

        if (asked.Length > MaxTargetIdLength)
        {
            return Invalid($"The id is {asked.Length} characters long; an OSDU id is at most {MaxTargetIdLength}.");
        }

        if (anyStorageId ? !TargetId.IsStorageId(asked) : !TargetId.IsRecordReference(asked) || asked.Any(char.IsControl))
        {
            return Invalid(anyStorageId
                ? $"'{asked}' is not an OSDU record id: a partition, a type and a unique part, separated by colons, each of letters, digits, _, - and . (the unique part also : and %)."
                : $"'{asked}' is not an OSDU record id: a partition, an entity type such as master-data--Wellbore, and a unique part, separated by colons.");
        }

        return InvalidVersion(request);
    }

    /// <summary>
    /// The operator's "Probe target", asked from the GUI, the API or the MCP tool: one interface's target probed in this
    /// process and recorded in the audit trail under the caller (<see cref="TargetProbes.RunAsync"/>, the path the scheduled
    /// probe takes too), answering with what the probe found.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ProbeAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, EngineContext engine, TimeProvider clock, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await TargetProbes.RunAsync(db, ledger, config, direct, engine, clock, loggers, flow, RequestActor.Label(user), ct).ConfigureAwait(false);
    }
}
