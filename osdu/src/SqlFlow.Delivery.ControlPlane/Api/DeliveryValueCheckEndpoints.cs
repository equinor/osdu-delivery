using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One interface of a delivery flow that renders with a mapping: the pipeline, the interface and the partition it is
/// described in, the ledger it keeps, the record table it reads and the route it delivers by. What a mapping's value check
/// is run against, since the rows it reads are a flow's.
/// </summary>
/// <param name="PipelineId">The flow's pipeline.</param>
/// <param name="Flow">The flow's name.</param>
/// <param name="Interface">The interface, or null for a flow in the single form.</param>
/// <param name="Partition">The partition the interface is described in, or null for a flow that names no partitions.</param>
/// <param name="LedgerFlowId">The ledger identity its records are kept under.</param>
/// <param name="RecordObject">The record table it reads.</param>
/// <param name="Route">The route it delivers by.</param>
public sealed record DeliveryMappingFlowDto(
    Guid PipelineId, string Flow, string? Interface, string? Partition, Guid LedgerFlowId, string RecordObject, string Route);

/// <summary>A column of a source table, as the catalog last described it.</summary>
public sealed record DeliverySourceColumnDto(string Name, string? DataType, bool Nullable);

/// <summary>
/// A table a flow reads: the record table, or the child dataset <c>Dataset</c> names, its name, how a child row joins its record
/// (child column to record column), and the columns the catalog holds of it, without SQLFlow's system columns; none when the
/// catalog has not described it.
/// </summary>
public sealed record DeliverySourceTableDto(
    string? Dataset, string Table, IReadOnlyList<string> Key, IReadOnlyDictionary<string, string>? Join, IReadOnlyList<DeliverySourceColumnDto> Columns);

/// <summary>
/// A value check of a flow's rows: the variables to check (template paths; none for every entry of the mapping), the flow
/// parameter values its scope is read with, how many rows to read (0 for the whole scope), how many example records each
/// finding names and how many it passes over first, and the mapping it is asked of, which the flow must render with.
/// </summary>
public sealed record DeliveryValueCheckRequest(
    IReadOnlyList<string>? Targets,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values,
    long? MaxRows,
    int? Samples,
    long? SkipSamples,
    string? Mapping,
    bool? DryRun = null);

/// <summary>
/// The T-SQL that shows what an attribute of a mapping reads: the attribute (a template path; none for every entry of the
/// mapping), the flow parameter values its scope is read with, the most rows a statement returns (0 for all of them), and the
/// records to narrow to, each a key as a JSON array of its parts.
/// </summary>
public sealed record DeliverySourceSqlRequest(
    string? Target,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values,
    long? MaxRows,
    IReadOnlyList<string>? Keys);

/// <summary>
/// A page of the rows staged in a flow's scope: the flow parameter values its scope is read with, the text a row must hold, where
/// the last page ended, and how many rows to a page.
/// </summary>
public sealed record DeliveryStagedRowsRequest(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values,
    string? Search,
    string? After,
    int? Limit);

/// <summary>
/// A look at the first rows of a table a flow reads: the flow parameter values its scope is read with, the child dataset to read
/// (none for the record table), and how many rows.
/// </summary>
public sealed record DeliveryTableRowsRequest(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values,
    string? Dataset,
    int? Limit);

/// <summary>
/// The value check: which flows render with a mapping, and the check of a flow's rows queued for a node
/// (<see cref="CheckValuesOperation"/>), which finds the rows that will not give the mapping's variables the values their
/// template expects. A request a node would refuse is a 400 here; whether a variable names something the mapping fills is
/// the node's to find, since the node reads the mapping the flow renders with.
/// </summary>
public static class DeliveryValueCheckEndpoints
{
    /// <summary>The most variables one check names; a check of every variable names none.</summary>
    public const int MaxTargets = 200;

    /// <summary>The most flows listed for one mapping.</summary>
    private const int MaxFlows = 500;

    /// <summary>The most columns listed of one table, however wide.</summary>
    private const int MaxSourceColumns = 2000;

    /// <summary>The longest mapping reference a check names.</summary>
    private const int MaxMappingLength = 200;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/mappings/{mappingId:guid}/flows", ListMappingFlowsAsync).WithName("ListDeliveryMappingFlows");
        delivery.MapGet("/flows/{pipelineId:guid}/source-tables", ListSourceTablesAsync).WithName("ListDeliveryFlowSourceTables");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/flows/{pipelineId:guid}/check-values", CheckValuesAsync).WithName("CheckDeliveryFlowValues");
        delivery.MapPost("/flows/{pipelineId:guid}/check-values/export", ExportIssuesAsync).WithName("ExportDeliveryFlowValueIssues");
        delivery.MapPost("/flows/{pipelineId:guid}/staged-rows", StagedRowsAsync).WithName("ListDeliveryFlowStagedRows");
        delivery.MapPost("/flows/{pipelineId:guid}/table-rows", TableRowsAsync).WithName("ReadDeliveryFlowTableRows");
        delivery.MapPost("/flows/{pipelineId:guid}/source-sql", SourceSqlAsync).WithName("WriteDeliveryFlowSourceSql");
    }

    /// <summary>
    /// The interfaces of the repository's delivery flows that render with the mapping, as the last sync described them, in
    /// every partition, ordered by flow, partition and interface. A mapping no flow renders with lists none.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryMappingFlowDto>>, ProblemHttpResult>> ListMappingFlowsAsync(
        Guid mappingId, CatalogDbContext db, OsduDbContext osdu, CancellationToken ct)
    {
        var mapping = await osdu.DeliveryMappings.AsNoTracking()
            .Where(m => m.Id == mappingId)
            .Select(m => new { m.RepoId, m.Reference })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (mapping is null)
        {
            return TypedResults.Problem(detail: $"No mapping '{mappingId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var interfaces = await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == mapping.RepoId && i.MappingReference == mapping.Reference && i.Active)
            .OrderBy(i => i.FlowName).ThenBy(i => i.Partition).ThenBy(i => i.Ordinal)
            .Take(MaxFlows)
            .ToListAsync(ct).ConfigureAwait(false);
        if (interfaces.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryMappingFlowDto>>([]);
        }

        var names = interfaces.Select(i => i.FlowName).Distinct(StringComparer.Ordinal).ToList();
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == mapping.RepoId && names.Contains(p.Name) && p.Kind == FlowDefinition.FlowTypeName && p.Active)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var byName = pipelines.ToDictionary(p => p.Name, p => p.Id, StringComparer.Ordinal);

        // An interface whose pipeline the catalog no longer holds as an active delivery flow has no rows a check can read.
        return TypedResults.Ok<IReadOnlyList<DeliveryMappingFlowDto>>(interfaces
            .Where(i => byName.ContainsKey(i.FlowName))
            .Select(i => new DeliveryMappingFlowDto(
                byName[i.FlowName],
                i.FlowName,
                i.Interface.Length == 0 ? null : i.Interface,
                i.Partition.Length == 0 ? null : i.Partition,
                i.LedgerFlowId,
                i.RecordObject,
                i.Route))
            .ToList());
    }

    /// <summary>
    /// The tables an interface reads, record table first: what its flow declares, with the columns the catalog holds of each. A
    /// table the catalog has not described lists no columns. No table is read.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliverySourceTableDto>>, ProblemHttpResult>> ListSourceTablesAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var source = flow.Flow.Source;
        var tables = new List<DeliverySourceTableDto>
        {
            new(null, source.Record.Object, source.Record.Key, null, await CatalogColumnsAsync(db, source.Record.Object, source.SystemColumns, ct).ConfigureAwait(false)),
        };

        foreach (var (name, dataset) in source.Datasets)
        {
            var columns = await CatalogColumnsAsync(db, dataset.Object, source.SystemColumns, ct).ConfigureAwait(false);
            tables.Add(new DeliverySourceTableDto(name, dataset.Object, [.. dataset.Join.Keys], dataset.Join, columns));
        }

        return TypedResults.Ok<IReadOnlyList<DeliverySourceTableDto>>(tables);
    }

    /// <summary>
    /// The columns the catalog holds of a table, in their order. One table can be catalogued once per connection it was reached
    /// through, so the columns are the first such object's.
    /// </summary>
    private static async Task<IReadOnlyList<DeliverySourceColumnDto>> CatalogColumnsAsync(
        CatalogDbContext db, string table, FlowSystemColumns system, CancellationToken ct)
    {
        var name = SourceObjectName.Parse(table);
        var keys = await db.Objects.AsNoTracking()
            .Where(o => o.Database == name.Database && o.Schema == name.Schema && o.Name == name.Name)
            .Select(o => o.Key)
            .ToListAsync(ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return [];
        }

        var rows = await db.ObjectColumns.AsNoTracking()
            .Where(c => keys.Contains(c.ObjectKey))
            .OrderBy(c => c.ObjectKey).ThenBy(c => c.Ordinal).ThenBy(c => c.Name)
            .Take(MaxSourceColumns)
            .Select(c => new { c.ObjectKey, c.Name, c.DataType, c.Nullable })
            .ToListAsync(ct).ConfigureAwait(false);
        var hidden = system.Names();
        return rows
            .Where(c => c.ObjectKey == rows[0].ObjectKey && !hidden.Contains(c.Name))
            .Select(c => new DeliverySourceColumnDto(c.Name, c.DataType, c.Nullable))
            .ToList();
    }

    /// <summary>
    /// A value check of one interface's rows, queued for a node with the variables, the scope's values, the row budget and
    /// the example records it is asked for. Every part of the request is checked here, so a request a node would refuse
    /// answers 400 before anything is queued.
    /// </summary>
    private static Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> CheckValuesAsync(
        Guid pipelineId, DeliveryValueCheckRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
        => QueueAsync(CheckValuesOperation.OperationName, pipelineId, request, interfaceName, partition, db, documents, partitions, dispatcher, user, ct);

    /// <summary>
    /// A page of the rows staged in one interface's scope, read in this process (<see cref="DirectOperationRunner"/>): what a
    /// mapping's Staged records tab lists, before anything renders. Every part of the request is checked here, so one a read
    /// would refuse answers 400 rather than a read that fails.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> StagedRowsAsync(
        Guid pipelineId, DeliveryStagedRowsRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (StagedArguments(flow.Flow, request ?? new DeliveryStagedRowsRequest(null, null, null, null), arguments) is { } refused)
        {
            return DeliveryEndpoints.Invalid(refused);
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, StagedRowsOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>Fills <paramref name="arguments"/> for <paramref name="request"/>, or says why a read would refuse it.</summary>
    internal static string? StagedArguments(FlowDefinition flow, DeliveryStagedRowsRequest request, Dictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(arguments);
        var values = request.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (DeliveryEndpoints.ParameterProblem(flow, values) is { } parameters)
        {
            return parameters;
        }

        if (values.Count > 0 && Json(values, "parameter values", arguments, "values") is { } tooLong)
        {
            return tooLong;
        }

        if (request.Limit is { } limit)
        {
            if (limit is < 1 or > StagedRowsOperation.MaxLimit)
            {
                return $"A page of staged rows holds between 1 and {StagedRowsOperation.MaxLimit} rows; {limit.ToString(CultureInfo.InvariantCulture)} is outside that.";
            }

            arguments[StagedRowsOperation.LimitArgument] = limit.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Search?.Trim() is { Length: > 0 } search)
        {
            if (search.Length > StagedRowsOperation.MaxSearch || search.Any(char.IsControl))
            {
                return $"A search of staged rows is one line of at most {StagedRowsOperation.MaxSearch} characters.";
            }

            arguments[StagedRowsOperation.SearchArgument] = search;
        }

        if (!string.IsNullOrWhiteSpace(request.After))
        {
            if (request.After.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return $"Where a page of staged rows starts is at most {ComputeTaskPayload.MaxArgumentLength} characters.";
            }

            arguments[StagedRowsOperation.AfterArgument] = request.After;
        }

        return null;
    }

    /// <summary>
    /// The first rows of one table an interface reads, every column of it, read in this process (<see cref="DirectOperationRunner"/>):
    /// what a mapping overview shows of a table it is asked to look into. Every part of the request is checked here, so one the
    /// read would refuse answers 400 rather than a read that fails.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> TableRowsAsync(
        Guid pipelineId, DeliveryTableRowsRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TableArguments(flow.Flow, request ?? new DeliveryTableRowsRequest(null, null, null), arguments) is { } refused)
        {
            return DeliveryEndpoints.Invalid(refused);
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, TableRowsOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>Fills <paramref name="arguments"/> for <paramref name="request"/>, or says why the read would refuse it.</summary>
    internal static string? TableArguments(FlowDefinition flow, DeliveryTableRowsRequest request, Dictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(arguments);
        var values = request.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (DeliveryEndpoints.ParameterProblem(flow, values) is { } parameters)
        {
            return parameters;
        }

        if (values.Count > 0 && Json(values, "parameter values", arguments, "values") is { } tooLong)
        {
            return tooLong;
        }

        if (request.Dataset is { } dataset)
        {
            if (!flow.Source.Datasets.ContainsKey(dataset))
            {
                return $"Flow '{flow.Label}' reads no child dataset '{dataset}'; it reads {(flow.Source.Datasets.Count == 0 ? "none" : string.Join(", ", flow.Source.Datasets.Keys.Order(StringComparer.Ordinal)))}.";
            }

            arguments[TableRowsOperation.DatasetArgument] = dataset;
        }

        if (request.Limit is { } limit)
        {
            if (limit is < 1 or > TableRowsOperation.MaxLimit)
            {
                return $"A look at a table reads between 1 and {TableRowsOperation.MaxLimit} rows; {limit.ToString(CultureInfo.InvariantCulture)} is outside that.";
            }

            arguments[TableRowsOperation.LimitArgument] = limit.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>
    /// Keys as they are, a quote or a letter outside ASCII taking one character: they travel as a task's argument and never reach
    /// a page, and what a page counts of them has to be what an argument's limit counts.
    /// </summary>
    private static readonly JsonSerializerOptions PlainJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The T-SQL showing what an attribute of a mapping reads, written in this process (<see cref="DirectOperationRunner"/>) from
    /// the shape of the flow's tables. No row is read and nothing is run: the script is for the reader to run. Every part of the
    /// request is checked here, so one the operation would refuse answers 400.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> SourceSqlAsync(
        Guid pipelineId, DeliverySourceSqlRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (SourceSqlArguments(flow.Flow, request ?? new DeliverySourceSqlRequest(null, null, null, null), arguments) is { } refused)
        {
            return DeliveryEndpoints.Invalid(refused);
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, SourceSqlOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>Fills <paramref name="arguments"/> for <paramref name="request"/>, or says why it cannot be written.</summary>
    internal static string? SourceSqlArguments(FlowDefinition flow, DeliverySourceSqlRequest request, Dictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(arguments);
        var values = request.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (DeliveryEndpoints.ParameterProblem(flow, values) is { } parameters)
        {
            return parameters;
        }

        if (values.Count > 0 && Json(values, "parameter values", arguments, "values") is { } tooLong)
        {
            return tooLong;
        }

        if (request.Target?.Trim() is { Length: > 0 } target)
        {
            if (!TemplatePath.TryParse(target, out _, out var error))
            {
                return $"'{target}' is not a variable a query can name: {error}.";
            }

            arguments[SourceSqlOperation.TargetArgument] = target;
        }

        if (request.MaxRows is { } rows)
        {
            if (rows < 0)
            {
                return $"A query returns a number of rows, or 0 for all of them; {rows.ToString(CultureInfo.InvariantCulture)} is neither.";
            }

            arguments[SourceSqlOperation.MaxRowsArgument] = rows.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Keys is { Count: > 0 } keys)
        {
            var parts = flow.Source.Record.Key.Count;
            var named = new List<IReadOnlyList<string>>(keys.Count);
            foreach (var key in keys)
            {
                if (KeyParts(key) is not { } held || held.Count != parts)
                {
                    return $"A record of this flow is named by a JSON array of its {parts.ToString(CultureInfo.InvariantCulture)} key parts, and '{key}' is not one.";
                }

                named.Add(held);
            }

            if (Json(named, "records named", arguments, SourceSqlOperation.KeysArgument, PlainJson) is { } tooMany)
            {
                return tooMany;
            }
        }

        return null;
    }

    /// <summary>The parts of a key, or null for text that is not a key.</summary>
    private static IReadOnlyList<string>? KeyParts(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            return KeyTuple.FromJson(key).Values;
        }
        catch (DeliveryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The same check, asked for as one is, queued to write every issue it finds to an Excel workbook
    /// (<see cref="ExportValueIssuesOperation"/>), which the task's result carries.
    /// </summary>
    private static Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> ExportIssuesAsync(
        Guid pipelineId, DeliveryValueCheckRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
        => QueueAsync(ExportValueIssuesOperation.OperationName, pipelineId, request, interfaceName, partition, db, documents, partitions, dispatcher, user, ct);

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> QueueAsync(
        string operation, Guid pipelineId, DeliveryValueCheckRequest? request, string? interfaceName, string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (Arguments(flow.Flow, request ?? new DeliveryValueCheckRequest(null, null, null, null, null, null), out var arguments) is { } refused)
        {
            return DeliveryEndpoints.Invalid(refused);
        }

        return await DeliveryEndpoints.EnqueueOperationAsync(db, dispatcher, flow, operation, arguments, user, ct).ConfigureAwait(false);
    }

    /// <summary>The task's arguments for <paramref name="request"/>, or why a node would refuse it.</summary>
    internal static string? Arguments(FlowDefinition flow, DeliveryValueCheckRequest request, out Dictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(request);
        arguments = new Dictionary<string, string>(StringComparer.Ordinal);

        var targets = (request.Targets ?? []).Select(t => t?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count > MaxTargets)
        {
            return $"A check names at most {MaxTargets} variables, and this one names {targets.Count}; name none to check every variable of the mapping.";
        }

        if (request.DryRun == true)
        {
            if (targets.Count > 0)
            {
                return "A dry run reads every attribute of the mapping, so it names none.";
            }

            arguments["dryRun"] = "true";
        }

        foreach (var target in targets)
        {
            if (!TemplatePath.TryParse(target, out _, out var error))
            {
                return $"'{target}' is not a variable a check can name: {error}.";
            }
        }

        if (targets.Count > 0 && Json(targets, "variables", arguments, "targets") is { } tooLong)
        {
            return tooLong;
        }

        var values = request.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (DeliveryEndpoints.ParameterProblem(flow, values) is { } parameters)
        {
            return parameters;
        }

        if (values.Count > 0 && Json(values, "parameter values", arguments, "values") is { } valuesTooLong)
        {
            return valuesTooLong;
        }

        if (request.MaxRows is { } rows)
        {
            if (rows < 0)
            {
                return $"A check reads a number of rows, or 0 for the whole scope; {rows.ToString(CultureInfo.InvariantCulture)} is neither.";
            }

            arguments["maxRows"] = rows.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Samples is { } samples)
        {
            if (samples < 0 || samples > ValueCheckLimits.Default.MaxSamples)
            {
                return $"A finding names between 0 and {ValueCheckLimits.Default.MaxSamples} example records; {samples.ToString(CultureInfo.InvariantCulture)} is outside that.";
            }

            arguments["samples"] = samples.ToString(CultureInfo.InvariantCulture);
        }

        if (request.SkipSamples is { } skip)
        {
            if (skip < 0)
            {
                return $"The example records a finding passes over are a count from 0; {skip.ToString(CultureInfo.InvariantCulture)} is not one.";
            }

            arguments["skipSamples"] = skip.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Mapping?.Trim() is { Length: > 0 } mapping)
        {
            if (mapping.Length > MaxMappingLength || mapping.Any(char.IsControl))
            {
                return "The mapping a check is asked of is named Name@version, on one line.";
            }

            if (!string.Equals(mapping, flow.Render.Mapping, StringComparison.Ordinal))
            {
                return $"Flow '{flow.Label}' renders with mapping {flow.Render.Mapping}, not {mapping}; check the mapping against a flow that renders with it.";
            }

            arguments["mapping"] = mapping;
        }

        return null;
    }

    /// <summary>Writes <paramref name="value"/> as the JSON argument <paramref name="name"/>, or says it is longer than a task carries.</summary>
    private static string? Json<T>(T value, string what, Dictionary<string, string> arguments, string name, JsonSerializerOptions? options = null)
    {
        var json = JsonSerializer.Serialize(value, options);
        if (json.Length > ComputeTaskPayload.MaxArgumentLength)
        {
            return $"The {what} are {json.Length.ToString(CultureInfo.InvariantCulture)} characters as JSON; a check carries at most {ComputeTaskPayload.MaxArgumentLength.ToString(CultureInfo.InvariantCulture)}. Check fewer at a time.";
        }

        arguments[name] = json;
        return null;
    }
}
