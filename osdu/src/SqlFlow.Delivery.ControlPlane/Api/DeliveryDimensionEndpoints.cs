using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>How the index stores a dimension's field, as its builds settled it.</summary>
public sealed record DeliveryDimensionFieldDto(string Index, string? NestedPath, string AggregateBy, bool Repeats);

/// <summary>What one build changed of a dimension: its values that arrived, left or came back, and its keys that arrived, left, moved or came back.</summary>
public sealed record DeliveryDimensionChangesDto(
    long ValuesAdded, long ValuesRemoved, long ValuesRestored, long KeysAdded, long KeysRemoved, long KeysMoved, long KeysRestored);

/// <summary>A kind a build read, with its records and the template it was read against.</summary>
public sealed record DeliveryDimensionKindDto(string Kind, long Records, string? Template);

/// <summary>
/// One build of a dimension: who ran it and what came of it, how it read (aggregations, ranges split, ranges scanned, count
/// queries, label searches), how complete the keys are (records, records with a key, nulls, keys too long for the index's
/// exact field, keys not of the field's type), how many keys it labelled, what it found, and what it changed. <c>Load</c>
/// says how it loaded the dimension, <c>full</c> or <c>incremental</c>; an incremental load read the records that changed
/// in <c>[WindowFrom, WindowTo)</c> (<c>ChangedRecords</c>) and read again <c>TouchedKeys</c> keys, and every build read
/// what changed up to <c>WindowTo</c>, which the next incremental load reads on from. For an incremental load
/// <c>Records</c> are the records that changed.
/// </summary>
public sealed record DeliveryDimensionBuildDto(
    long BuildId, int DimensionId, Guid? RunId, string Actor, string Status, DateTime StartedUtc, DateTime? CompletedUtc, string? Error,
    string DefinitionHash, string? Query, string? AggregateBy, long Values, long Keys, long LeftOut, long Unfilterable,
    long? Records, long? WithValue, long Nulls, long? TooLong, long Unreadable,
    int Aggregations, int Slices, int Splits, int ScannedSlices, int ScanPages, long ScannedUnits, int CountQueries,
    long Labelled, long Unlabelled, int LabelQueries,
    IReadOnlyList<DeliveryDimensionKindDto> Kinds, IReadOnlyList<string> Notes, DeliveryDimensionChangesDto Changes,
    string Load, DateTime? WindowFrom, DateTime? WindowTo, long? ChangedRecords, long? TouchedKeys);

/// <summary>
/// How a dimension flow's builds load its dimensions after their first full load, as its <c>incremental</c> block says:
/// how far behind now a window ends (<c>LagMinutes</c>), how old a dimension's last full load may be before a build loads
/// it in full again (<c>FullLoadAfterHours</c>, null for never on its own), and whether every build loads in full
/// (<c>FullLoad</c>).
/// </summary>
public sealed record DeliveryDimensionIncrementalDto(int LagMinutes, int? FullLoadAfterHours, bool FullLoad);

/// <summary>
/// One dimension of a dimension flow in a partition: what the flow declares of it (or, for one it no longer declares, what
/// its last build read with), where a key's label is read (<c>Label</c>: the paths through the records a key names), how
/// the index stores its field, how many values and keys it holds now, the build that wrote that (<c>Current</c>), and the
/// newest build when that is another one (<c>Latest</c>: one that failed, was cancelled or is running). <c>DimensionId</c> is
/// null for a dimension no build has registered yet. <c>Changed</c> is true when the declaration differs from the one its
/// values were built with. <c>Table</c> is the dimension's own table (<c>osdu.dim_...</c>): the dimension as one table, a
/// row per key and value it collects; null until a build has written it. <c>KeyColumn</c> and <c>ValueColumn</c> are what
/// the dimension calls its key and its value, and what the two columns of its table are named after its next build: the
/// property its path ends with (with <c>Key</c> at its end where the value's has that name), and the property its
/// label ends with (or its own name, when it reads no label), unless the flow names them.
/// </summary>
public sealed record DeliveryDimensionDto(
    int? DimensionId, string Name, string? Description, string Kind, string? Query, string? BuiltQuery, string Path, IReadOnlyList<string> Label,
    string? Unlabelled, IReadOnlyList<DeliveryDimensionAttributeSpecDto> Attributes, IReadOnlyList<string> Clean, bool CountRecords, long MaxValues, bool Declared,
    bool BuildsHere, bool Changed, DeliveryDimensionFieldDto? Field, long Values, long Keys, DateTime? LastBuiltUtc, DeliveryDimensionBuildDto? Current,
    DeliveryDimensionBuildDto? Latest, string? Table, string KeyColumn, string ValueColumn, DateTime? LastFullLoadUtc);

/// <summary>
/// One row of a dimension's table: the row's number (what a table of facts joins on), the key's number, the key, its
/// value, its attributes in the order of the table's <c>Attributes</c> (null where the key has none), the records of the
/// row, and the search filter finding the key's records.
/// </summary>
public sealed record DeliveryDimensionTableRowDto(long Id, long KeyId, string Key, string Value, IReadOnlyList<string?> Attributes, long Records, string? Filter);

/// <summary>
/// A page of a dimension's table: the table's name, the names of the columns that hold each row's <c>Key</c> and its
/// <c>Value</c> as the table has them now, its attribute columns, the rows, whether more follow, and on a first page how
/// many rows the query matches in all.
/// </summary>
public sealed record DeliveryDimensionTableDto(
    string Table, string KeyColumn, string ValueColumn, IReadOnlyList<string> Attributes, IReadOnlyList<DeliveryDimensionTableRowDto> Rows, bool More,
    long? Total);

/// <summary>
/// An attribute a dimension reads of its keys: its name, and the paths it is read through from the record a key names, or
/// the path of the dimension's own records whose values it collects (<c>collect</c>, with no steps). <c>keep</c> is
/// <c>value</c> where each value is shown as a value, <c>key</c> where it is kept exactly as the record holds it, and
/// <c>id</c> where a reference is kept as the id of the record it names: the last two are what dimension tables join on.
/// <c>many</c> is how the values its last step reaches are kept (<c>first</c>, <c>join</c>, <c>all</c> or <c>max</c>),
/// <c>decode</c> what each is decoded from (<c>none</c> or <c>url</c>), <c>maxReferences</c> how many records a step follows
/// for one key, and <c>trim</c> whether a value loses the white space at its ends.
/// </summary>
public sealed record DeliveryDimensionAttributeSpecDto(
    string Name, IReadOnlyList<string> Steps, string? Collect, string Keep, string Many, string Decode, int MaxReferences, bool Trim);

/// <summary>
/// A value of an attribute of a key: the value, where it was read (the id of the record it was read from, or the text the
/// key's records hold for a collected attribute), and for a collected attribute how many of the key's records hold it.
/// </summary>
public sealed record DeliveryDimensionAttributeDto(string Name, string Value, string? From, long? Records);

/// <summary>A value an attribute holds among a value's keys, with how many of them hold it.</summary>
public sealed record DeliveryDimensionValueAttributeDto(string Name, string Value, int Keys);

/// <summary>A value an attribute holds among a dimension's keys: the keys holding it and their records (summed).</summary>
public sealed record DeliveryDimensionAttributeValueDto(string Value, int Keys, long Records);

/// <summary>What removing a dimension took out of the ledger: the dimension, the rows kept of it in each table, and one line saying so.</summary>
public sealed record DeliveryDimensionRemovedDto(
    int DimensionId, string Dimension, string Flow, string? Partition, long Values, long Keys, long Builds, long Changes, long Attributes, long Texts, string Summary);

/// <summary>
/// One dimension flow in the partition a board is read in: its dimensions, the partitions it builds in and whether the
/// board's is one, the parameters a run of it takes, what keeps it from being shown (a document the catalog cannot parse,
/// a partition it does not build in), and how its builds load (<c>Incremental</c>, null for a flow whose every build loads
/// in full).
/// </summary>
public sealed record DeliveryDimensionFlowDto(
    Guid PipelineId, Guid RepoId, string Name, string? Description, string? Batch, string? Partition, bool BuildsPartition, IReadOnlyList<string> Partitions,
    Guid? LedgerId, IReadOnlyList<DeliveryParameterDto> Parameters, string? Problem, IReadOnlyList<DeliveryDimensionDto> Dimensions,
    DeliveryDimensionIncrementalDto? Incremental);

/// <summary>What a board adds up to, over the dimensions of the flows that build in its partition.</summary>
public sealed record DeliveryDimensionTotalsDto(
    int Flows, int Dimensions, int Built, int NotBuilt, int Failing, int Running, int Changed, long Values, long Keys);

/// <summary>The dimensions of every dimension flow, or of one, in a partition.</summary>
public sealed record DeliveryDimensionBoardDto(string? Partition, DeliveryDimensionTotalsDto Totals, IReadOnlyList<DeliveryDimensionFlowDto> Flows);

/// <summary>One dimension with the flow that declares it: the pipeline to build it with, when the catalog still holds the flow.</summary>
public sealed record DeliveryDimensionDetailDto(
    Guid? PipelineId, Guid? RepoId, string FlowName, Guid LedgerId, string? Partition, IReadOnlyList<DeliveryParameterDto> Parameters, DeliveryDimensionDto Dimension);

/// <summary>A key as a list of values shows it beside its value: the key, its label, and its count.</summary>
public sealed record DeliveryDimensionKeyBriefDto(string Key, string? Label, long Count);

/// <summary>
/// A value of a dimension: the human-friendly value a person picks, the records holding any of its keys (exact, or the sum of
/// its keys' counts), how many keys it stands for and how many of them no query can carry, the search filter finding its
/// records when one query holds it (<c>FilterParts</c> queries otherwise), when it arrived and when a build stopped finding
/// it, the keys most records hold (<c>Top</c>), and the values its keys' attributes hold (the most keys first).
/// </summary>
public sealed record DeliveryDimensionValueDto(
    long ValueId, string Value, long Records, bool RecordsExact, int Keys, int Unfilterable, string? Filter, int FilterParts,
    long FirstSeenBuildId, DateTime FirstSeenUtc, long? RemovedBuildId, DateTime? RemovedUtc, IReadOnlyList<DeliveryDimensionKeyBriefDto> Top,
    IReadOnlyList<DeliveryDimensionValueAttributeDto> Attributes);

/// <summary>A page of values, with the cursor of the next page; null when this is the last.</summary>
public sealed record DeliveryDimensionValuePageDto(IReadOnlyList<DeliveryDimensionValueDto> Items, string? Next);

/// <summary>
/// A key of a dimension: exactly what the OSDU index holds (an id for a reference), what a search compares; the label read
/// for it from the record it names and that record's id; the value it belongs to, or why it belongs to none; its count; and
/// the search filter finding exactly the records holding it.
/// </summary>
public sealed record DeliveryDimensionKeyDto(
    long KeyId, string Key, string? Label, string? LabelFrom, long? ValueId, string? Value, string? LeftOut, string? Note, long Count, bool Filterable,
    string? Filter, long FirstSeenBuildId, DateTime FirstSeenUtc, long ValueSinceBuildId, long? RemovedBuildId, DateTime? RemovedUtc,
    IReadOnlyList<DeliveryDimensionAttributeDto> Attributes);

/// <summary>A page of keys, with the cursor of the next page; null when this is the last.</summary>
public sealed record DeliveryDimensionKeyPageDto(IReadOnlyList<DeliveryDimensionKeyDto> Items, string? Next);

/// <summary>A change a build made to one key: it arrived, left, came back, or moved from one value to another.</summary>
public sealed record DeliveryDimensionChangeDto(
    long ChangeId, long BuildId, long KeyId, string Key, string Change, long? FromValueId, string? FromValue, long? ToValueId, string? ToValue,
    DateTime ChangedUtc);

/// <summary>A page of the change log, newest first, with the change the next page starts before; null when this is the last.</summary>
public sealed record DeliveryDimensionChangePageDto(IReadOnlyList<DeliveryDimensionChangeDto> Items, long? Next);

/// <summary>The values a filter is asked for by: by id, by value, or both.</summary>
public sealed record DeliveryDimensionFilterRequest(IReadOnlyList<long>? ValueIds, IReadOnlyList<string>? Values);

/// <summary>A value a filter covers.</summary>
public sealed record DeliveryDimensionFilterValueDto(long ValueId, string Value, long Records, bool RecordsExact, int Keys);

/// <summary>
/// The search filter of a set of values: the kind to search, the filter queries alone and each joined with the dimension's
/// own query (the searches to send), the values it covers and the keys it compares, and what it leaves out: keys no query
/// can carry, values no build finds any more, and names that are no value.
/// </summary>
public sealed record DeliveryDimensionFilterDto(
    string Kind, string? Query, string AggregateBy, IReadOnlyList<string> Filters, IReadOnlyList<string> Searches, IReadOnlyList<DeliveryDimensionFilterValueDto> Values,
    int Keys, int Unfilterable, IReadOnlyList<string> UnfilterableNamed, IReadOnlyList<string> Removed, IReadOnlyList<string> Missing);

/// <summary>
/// A value with the whole of what the ledger holds of it: its keys, most records first, each with its label and filter; the
/// value's filter, or why none can be written; and the changes that brought keys to it or took them away, newest first.
/// </summary>
public sealed record DeliveryDimensionValueDetailDto(
    DeliveryDimensionValueDto Value, IReadOnlyList<DeliveryDimensionKeyDto> Keys, bool MoreKeys, DeliveryDimensionFilterDto? Filter,
    string? FilterProblem, IReadOnlyList<DeliveryDimensionChangeDto> History);

/// <summary>A build a platform run made, with the name of the dimension it built.</summary>
public sealed record DeliveryDimensionRunBuildDto(string Dimension, DeliveryDimensionBuildDto Build);

/// <summary>
/// What is picked in one dimension: values by id, by value, or both, and attribute values its keys hold (with values too,
/// the keys of those values that hold them).
/// </summary>
public sealed record DeliveryDimensionPickRequest(
    int DimensionId, IReadOnlyList<long>? ValueIds, IReadOnlyList<string>? Values, IReadOnlyList<DeliveryDimensionAttributePickRequest>? Attributes = null);

/// <summary>An attribute picked: its name, and the values a key has to hold one of.</summary>
public sealed record DeliveryDimensionAttributePickRequest(string Name, IReadOnlyList<string>? Values);

/// <summary>
/// A search to compose: the values picked in each dimension, the kind to search (left out, the one kind every dimension
/// reads), and a query narrowing it further.
/// </summary>
public sealed record DeliveryDimensionSearchRequest(IReadOnlyList<DeliveryDimensionPickRequest>? Picks, string? Kind, string? Within);

/// <summary>
/// One dimension's part of a composed search: the values it covers, the keys compared, its filter, and the attribute values
/// its keys were picked by.
/// </summary>
public sealed record DeliveryDimensionSearchPartDto(
    int DimensionId, string Dimension, string AggregateBy, IReadOnlyList<DeliveryDimensionFilterValueDto> Values, int Keys, int Unfilterable, string Filter,
    string? Query, IReadOnlyList<DeliveryDimensionAttributePickRequest> Attributes);

/// <summary>
/// A composed search: the kind and the query to send, the request body the search service takes, each dimension's part, how
/// many clauses the query holds, and what the picks left out.
/// </summary>
public sealed record DeliveryDimensionSearchDto(
    string Kind, string Query, string Request, IReadOnlyList<DeliveryDimensionSearchPartDto> Parts, int Clauses, IReadOnlyList<string> Removed,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Notes);

/// <summary>
/// The dimensions of dimension flows (osdu/docs/reference/concepts/api.md, Assertions, dimensions and inventories): boards
/// across every dimension flow and for one, a dimension with its declaration and builds, its values and keys a page at a time
/// (searched, in value order or with the most records first), a value with its keys, filter and history, the change log, the
/// filter of any set of values, the search composed from values picked across a kind's dimensions, an export of the whole,
/// and the builds of a platform run. A key is exactly what the OSDU index holds, what a search compares; a value is the
/// human-friendly form a person picks, read from the record a key names or cleaned from the key; every key and value carries
/// the filter that finds its records. Every read answers from the ledger; nothing here talks to OSDU, and building a
/// dimension is a run like any other.
/// </summary>
public static partial class DeliveryDimensionEndpoints
{
    /// <summary>The values or keys a page holds when the request names no limit.</summary>
    public const int DefaultPage = 100;

    /// <summary>The builds a dimension's history lists when the request names no limit.</summary>
    public const int DefaultBuilds = 30;

    /// <summary>The most dimension flows one board shows.</summary>
    private const int MaxFlows = 500;

    /// <summary>The longest search text: a key is at most this long.</summary>
    private const int MaxSearchLength = 1024;

    /// <summary>The keys a page of values shows beside each value.</summary>
    private const int TopKeys = 5;

    /// <summary>The keys a value's page lists; a value with more pages through the keys list.</summary>
    private const int ValueKeys = 500;

    /// <summary>The changes a value's page lists.</summary>
    private const int ValueHistory = 200;

    private static readonly string[] ChangeKinds =
        [DimensionChangeKinds.Added, DimensionChangeKinds.Removed, DimensionChangeKinds.Moved, DimensionChangeKinds.Restored];

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/dimensions", GetBoardAsync).WithName("GetDeliveryDimensionBoard");
        delivery.MapGet("/flows/{pipelineId:guid}/dimensions", GetFlowBoardAsync).WithName("GetDeliveryDimensionFlowBoard");
        delivery.MapGet("/flows/{pipelineId:guid}/dimensions/{name}/blueprint", GetBlueprintAsync).WithName("GetDeliveryDimensionBlueprint");
        delivery.MapGet("/dimensions/{dimensionId:int}", GetDimensionAsync).WithName("GetDeliveryDimension");
        delivery.MapGet("/dimensions/{dimensionId:int}/table", ReadTableAsync).WithName("ReadDeliveryDimensionTable");
        delivery.MapGet("/dimensions/{dimensionId:int}/values", ListValuesAsync).WithName("ListDeliveryDimensionValues");
        delivery.MapGet("/dimensions/{dimensionId:int}/values/{valueId:long}", GetValueAsync).WithName("GetDeliveryDimensionValue");
        delivery.MapGet("/dimensions/{dimensionId:int}/keys", ListKeysAsync).WithName("ListDeliveryDimensionKeys");
        delivery.MapGet("/dimensions/{dimensionId:int}/attributes/{name}", ListAttributeValuesAsync).WithName("ListDeliveryDimensionAttributeValues");
        delivery.MapGet("/dimensions/{dimensionId:int}/builds", ListBuildsAsync).WithName("ListDeliveryDimensionBuilds");
        delivery.MapGet("/dimensions/{dimensionId:int}/changes", ListChangesAsync).WithName("ListDeliveryDimensionChanges");
        delivery.MapGet("/dimensions/{dimensionId:int}/export", ExportAsync).WithName("ExportDeliveryDimension");
        delivery.MapGet("/runs/{runId:guid}/dimension-builds", ListRunBuildsAsync).WithName("ListDeliveryRunDimensionBuilds");

        // Writing a filter or composing a search reads the ledger and changes nothing; the picks ride in the body, since a
        // set of them can be long.
        delivery.MapPost("/dimensions/{dimensionId:int}/filter", FilterAsync).WithName("GetDeliveryDimensionFilter");
        delivery.MapPost("/dimensions/search", SearchAsync).WithName("ComposeDeliveryDimensionSearch");
    }

    /// <summary>The routes that change what the ledger keeps of a dimension: removing one, an admin's alone.</summary>
    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapDelete("/dimensions/{dimensionId:int}", RemoveAsync).WithName("RemoveDeliveryDimension").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>
    /// Removes a dimension its flow no longer declares, and everything kept of it in its partition, for good
    /// (<see cref="DimensionRemoval"/>). A dimension the flow declares is refused, and so is one whose flow cannot be read
    /// now (whether it still declares the dimension cannot be told), or that a cache flow captures.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionRemovedDto>, ProblemHttpResult>> RemoveAsync(
        int dimensionId, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension)
        {
            return NoDimension(dimensionId);
        }

        var declaration = await DeclarationAsync(db, documents, dimension, ct).ConfigureAwait(false);
        if (declaration.Spec is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "Still declared",
                $"Flow {dimension.FlowName} declares dimension {dimension.Name}, so it is not removed. Take it out of the flow's YAML and sync the repository first.");
        }

        if (declaration.Unreadable is { } unreadable)
        {
            return Problem(StatusCodes.Status409Conflict, "Flow not readable",
                $"Flow {dimension.FlowName} ({unreadable}) cannot be read now, so whether it still declares dimension {dimension.Name} cannot be told; nothing is removed. Fix the flow and sync the repository first.");
        }

        try
        {
            var removed = await DimensionRemoval.RemoveAsync(ledger, dimension, RequestActor.Label(user), clock, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryDimensionRemovedDto(
                removed.DimensionId, removed.Name, removed.FlowName, removed.Partition, removed.Values, removed.Keys, removed.Builds, removed.Changes,
                removed.Attributes, removed.Texts, removed.Describe()));
        }
        catch (DeliveryException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Not removed", ex.Message);
        }
    }

    /// <summary>Every active dimension flow of the catalog, each in the partition the request reads.</summary>
    private static async Task<Ok<DeliveryDimensionBoardDto>> GetBoardAsync(
        string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry,
        CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active)
            .OrderBy(p => p.Name)
            .Take(MaxFlows)
            .ToListAsync(ct).ConfigureAwait(false);
        var named = WorkbenchPartition.Named(partition, request);
        var flows = await BoardAsync(pipelines, named, documents, ledger, registry, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionBoardDto(named, Totals(flows), flows));
    }

    /// <summary>One dimension flow's board.</summary>
    private static async Task<Results<Ok<DeliveryDimensionBoardDto>, ProblemHttpResult>> GetFlowBoardAsync(
        Guid pipelineId, string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status409Conflict, "Not a dimension flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a dimension flow.");
        }

        var named = WorkbenchPartition.Named(partition, request);
        var flows = await BoardAsync([pipeline], named, documents, ledger, registry, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionBoardDto(flows[0].Partition ?? named, Totals(flows), flows));
    }

    private static async Task<Results<Ok<DeliveryDimensionDetailDto>, ProblemHttpResult>> GetDimensionAsync(
        int dimensionId, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        var (pipeline, flow, spec, _) = await DeclarationAsync(db, documents, dimension, ct).ConfigureAwait(false);
        var builds = await BuildsAsync(ledger, [dimension], ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionDetailDto(
            pipeline?.Id, pipeline?.RepoId, dimension.FlowName, dimension.FlowId, dimension.Partition, flow is null ? [] : Parameters(flow),
            ToDto(spec, dimension.Partition, dimension, builds)));
    }

    /// <summary>
    /// A page of the dimension's table (osdu/docs/reference/flow/dimension-table.md, The dimension's table): the rows holding
    /// <c>search</c> in the key, the value or an attribute, and every attribute value asked for (<c>attr=Name:value</c>),
    /// ordered by <c>order</c> (a column of the table by its name, or <c>value</c> and <c>key</c> for those two columns
    /// whatever the dimension names them) ascending or, with <c>dir=desc</c>, descending, from row <c>offset</c>. A
    /// dimension built before dimensions had a table has its table made here.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionTableDto>, ProblemHttpResult>> ReadTableAsync(
        int dimensionId, string? search, string[]? attr, string? order, string? dir, int? offset, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (SearchProblem(search) is { } badSearch)
        {
            return badSearch;
        }

        if (order is { Length: > DimensionAttributeSpec.MaxNameLength })
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown column", $"order is a column of the table: at most {DimensionAttributeSpec.MaxNameLength} characters.");
        }

        var descending = false;
        switch (dir?.Trim().ToLowerInvariant())
        {
            case null or "" or "asc":
                break;
            case "desc":
                descending = true;
                break;
            default:
                return Problem(StatusCodes.Status400BadRequest, "Unknown direction", $"dir '{dir}' is not one of asc, desc.");
        }

        if (offset is < 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid offset", "offset is the number of rows before the page, zero or more.");
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension)
        {
            return NoDimension(dimensionId);
        }

        // The columns are the table's own, which the store checks the names against: a build that failed after its
        // declaration changed leaves the table with the attributes of the last one that wrote.
        var (matches, badAttribute) = AttributeMatches(attr, name => name, _ => string.Empty);
        if (badAttribute is not null)
        {
            return badAttribute;
        }

        try
        {
            var page = await DimensionTable.ReadAsync(
                ledger, dimension, new DimensionTableQuery(search, matches, order, descending, offset ?? 0, PageSize(limit)), ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryDimensionTableDto(
                page.Table,
                page.KeyColumn,
                page.ValueColumn,
                page.Attributes,
                page.Rows.Select(r => new DeliveryDimensionTableRowDto(r.Id, r.KeyId, r.Key, r.Value, r.Attributes, r.Records, r.Filter)).ToList(),
                page.More,
                page.Total));
        }
        catch (DeliveryException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, "No table", ex.Message);
        }
    }

    private static async Task<Results<Ok<DeliveryDimensionValuePageDto>, ProblemHttpResult>> ListValuesAsync(
        int dimensionId, string? search, string? order, bool? removed, string? after, int? limit, string[]? attr, ILedger ledger, CancellationToken ct)
    {
        if (SearchProblem(search) is { } badSearch)
        {
            return badSearch;
        }

        DimensionMemberOrder sorted;
        switch (order?.Trim().ToLowerInvariant())
        {
            case null or "" or "value":
                sorted = DimensionMemberOrder.Value;
                break;
            case "records":
                sorted = DimensionMemberOrder.Records;
                break;
            default:
                return Problem(StatusCodes.Status400BadRequest, "Unknown order", $"order '{order}' is not one of value, records.");
        }

        DimensionMemberCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            cursor = ValueCursorOf(after);
            if (cursor is null)
            {
                return BadCursor(after);
            }
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension)
        {
            return NoDimension(dimensionId);
        }

        var (matches, badAttribute) = AttributeMatches(dimension, attr);
        if (badAttribute is not null)
        {
            return badAttribute;
        }

        var take = PageSize(limit);
        var members = await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(search, removed ?? false, cursor, take, sorted, matches), ct).ConfigureAwait(false);
        var top = (await ledger.TopMemberOriginalsAsync(dimensionId, members.Select(m => m.MemberId).ToList(), TopKeys, ct).ConfigureAwait(false))
            .GroupBy(v => v.MemberId ?? 0)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DeliveryDimensionKeyBriefDto>)g
                .OrderByDescending(v => v.Count).ThenBy(v => v.ValueId)
                .Select(v => new DeliveryDimensionKeyBriefDto(v.Original, v.Label, v.Count)).ToList());
        var items = members.Select(m => ToDto(m, top.GetValueOrDefault(m.MemberId) ?? [])).ToList();
        return TypedResults.Ok(new DeliveryDimensionValuePageDto(items, members.Count == take ? ValueCursor(members[^1]) : null));
    }

    private static async Task<Results<Ok<DeliveryDimensionValueDetailDto>, ProblemHttpResult>> GetValueAsync(
        int dimensionId, long valueId, ILedger ledger, CancellationToken ct)
    {
        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        var named = await ledger.GetDimensionMembersAsync(dimensionId, [valueId], [], ct).ConfigureAwait(false);
        var member = named.Count > 0 ? named[0] : null;
        if (member is not null
            && (await ledger.MemberAttributesAsync(dimensionId, [valueId], OsduLedger.MemberAttributeValues, ct).ConfigureAwait(false)) is [var held, ..])
        {
            member = member with { Attributes = held.Attributes };
        }
        if (member is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Dimension {dimension.Name} has no value {valueId}.");
        }

        var keys = await ledger.ListDimensionValuesAsync(
            dimensionId, new DimensionValueQuery(null, valueId, false, false, null, ValueKeys + 1, DimensionValueOrder.Count), ct).ConfigureAwait(false);
        var history = await ledger.ListDimensionChangesAsync(dimensionId, new DimensionChangeQuery(null, null, valueId, null, null, ValueHistory), ct).ConfigureAwait(false);
        DeliveryDimensionFilterDto? filter = null;
        string? filterProblem = null;
        if (member.RemovedRunId is not null)
        {
            filterProblem = "No build finds this value any more, so it stands for no key a filter could find.";
        }
        else
        {
            try
            {
                filter = ToDto(await DimensionFilters.ForMembersAsync(ledger, dimension, [valueId], [], ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is DeliveryException or OsduQueryException)
            {
                filterProblem = ex.Message;
            }
        }

        var top = keys.Take(TopKeys).Select(v => new DeliveryDimensionKeyBriefDto(v.Original, v.Label, v.Count)).ToList();
        return TypedResults.Ok(new DeliveryDimensionValueDetailDto(
            ToDto(member, top), keys.Take(ValueKeys).Select(ToDto).ToList(), keys.Count > ValueKeys, filter, filterProblem, history.Select(ToDto).ToList()));
    }

    private static async Task<Results<Ok<DeliveryDimensionKeyPageDto>, ProblemHttpResult>> ListKeysAsync(
        int dimensionId, string? search, long? value, bool? leftOut, bool? removed, string? order, string? after, int? limit, string[]? attr, ILedger ledger,
        CancellationToken ct)
    {
        if (SearchProblem(search) is { } badSearch)
        {
            return badSearch;
        }

        DimensionValueOrder sorted;
        switch (order?.Trim().ToLowerInvariant())
        {
            case null or "" or "arrival":
                sorted = DimensionValueOrder.Arrival;
                break;
            case "count":
                sorted = DimensionValueOrder.Count;
                break;
            default:
                return Problem(StatusCodes.Status400BadRequest, "Unknown order", $"order '{order}' is not one of arrival, count.");
        }

        if (value is not null && leftOut == true)
        {
            return Problem(StatusCodes.Status400BadRequest, "Conflicting filters", "A key of a value is not left out: ask for a value's keys or for those of no value, not both.");
        }

        DimensionValueCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            cursor = KeyCursorOf(after);
            if (cursor is null)
            {
                return BadCursor(after);
            }
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension)
        {
            return NoDimension(dimensionId);
        }

        var (matches, badAttribute) = AttributeMatches(dimension, attr);
        if (badAttribute is not null)
        {
            return badAttribute;
        }

        var take = PageSize(limit);
        var keys = await ledger.ListDimensionValuesAsync(
            dimensionId, new DimensionValueQuery(search, value, leftOut ?? false, removed ?? false, cursor, take, sorted, matches), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionKeyPageDto(keys.Select(ToDto).ToList(), keys.Count == take ? KeyCursor(keys[^1]) : null));
    }

    /// <summary>
    /// The values an attribute of the dimension holds among its keys a build finds now, the most records first: among the
    /// keys holding every other attribute value asked for (<c>attr=Name:value</c>) and belonging to one of the values asked
    /// for (<c>value=</c> ids), so each select of a cascade lists what the other picks leave. A value asked for the attribute
    /// itself does not narrow its own list.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryDimensionAttributeValueDto>>, ProblemHttpResult>> ListAttributeValuesAsync(
        int dimensionId, string name, string? search, int? limit, string[]? attr, long[]? value, ILedger ledger, CancellationToken ct)
    {
        if (SearchProblem(search) is { } badSearch)
        {
            return badSearch;
        }

        if (value is { Length: > DimensionFilters.MaxMembersPerFilter })
        {
            return Problem(StatusCodes.Status400BadRequest, "Too many values", $"An attribute's values are narrowed by at most {DimensionFilters.MaxMembersPerFilter} values.");
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension)
        {
            return NoDimension(dimensionId);
        }

        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        var attribute = declared.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            return Problem(StatusCodes.Status404NotFound, "No such attribute", NoAttribute(dimension, name, declared));
        }

        var (matches, badAttribute) = AttributeMatches(dimension, attr);
        if (badAttribute is not null)
        {
            return badAttribute;
        }

        var values = await ledger.ListDimensionAttributeValuesAsync(
            dimensionId, new DimensionAttributeValueQuery(attribute.Name, search, PageSize(limit), matches, value), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryDimensionAttributeValueDto>>(values.Select(v => new DeliveryDimensionAttributeValueDto(v.Value, v.Keys, v.Records)).ToList());
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryDimensionBuildDto>>, ProblemHttpResult>> ListBuildsAsync(
        int dimensionId, int? max, ILedger ledger, CancellationToken ct)
    {
        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is null)
        {
            return NoDimension(dimensionId);
        }

        var runs = await ledger.ListDimensionRunsAsync(dimensionId, Math.Clamp(max ?? DefaultBuilds, 1, 500), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryDimensionBuildDto>>(runs.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliveryDimensionChangePageDto>, ProblemHttpResult>> ListChangesAsync(
        int dimensionId, long? build, long? key, long? value, string? change, long? before, int? limit, ILedger ledger, CancellationToken ct)
    {
        var kind = string.IsNullOrWhiteSpace(change) ? null : change.Trim().ToLowerInvariant();
        if (kind is not null && !ChangeKinds.Contains(kind, StringComparer.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown change", $"change '{change}' is not one of {string.Join(", ", ChangeKinds)}.");
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is null)
        {
            return NoDimension(dimensionId);
        }

        var take = PageSize(limit);
        var changes = await ledger.ListDimensionChangesAsync(dimensionId, new DimensionChangeQuery(build, key, value, kind, before, take), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionChangePageDto(changes.Select(ToDto).ToList(), changes.Count == take ? changes[^1].ChangeId : null));
    }

    private static async Task<Results<Ok<DeliveryDimensionFilterDto>, ProblemHttpResult>> FilterAsync(
        int dimensionId, DeliveryDimensionFilterRequest? body, ILedger ledger, CancellationToken ct)
    {
        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        var ids = body?.ValueIds ?? [];
        var values = (body?.Values ?? []).Where(v => v is not null).ToList();
        if (values.Any(v => v.Length > DimensionSpec.MaxCleanLength))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid value", $"A value is at most {DimensionSpec.MaxCleanLength} characters.");
        }

        try
        {
            return TypedResults.Ok(ToDto(await DimensionFilters.ForMembersAsync(ledger, dimension, ids, values, ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is DeliveryException or OsduQueryException)
        {
            return Problem(StatusCodes.Status400BadRequest, "No filter", ex.Message);
        }
    }

    /// <summary>
    /// The OSDU search finding the records that hold one of the values picked in each dimension: OR within a dimension, AND
    /// across dimensions, within each dimension's own query, in the kind every dimension reads (or the one the request names).
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionSearchDto>, ProblemHttpResult>> SearchAsync(
        DeliveryDimensionSearchRequest? body, ILedger ledger, CancellationToken ct)
    {
        var requested = body?.Picks ?? [];
        if (requested.Count == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Nothing picked", "A search is composed from at least one value picked in a dimension.");
        }

        if (requested.Count > DimensionSearch.MaxDimensions)
        {
            return Problem(StatusCodes.Status400BadRequest, "Too many dimensions", string.Create(CultureInfo.InvariantCulture, $"A search combines at most {DimensionSearch.MaxDimensions} dimensions."));
        }

        if (body?.Within is { Length: > 4000 })
        {
            return Problem(StatusCodes.Status400BadRequest, "Query too long", "The query narrowing a search is at most 4000 characters.");
        }

        var picks = new List<DimensionPick>(requested.Count);
        foreach (var pick in requested)
        {
            var dimension = await ledger.GetDimensionAsync(pick.DimensionId, ct).ConfigureAwait(false);
            if (dimension is null)
            {
                return NoDimension(pick.DimensionId);
            }

            var values = (pick.Values ?? []).Where(v => v is not null).ToList();
            if (values.Any(v => v.Length > DimensionSpec.MaxCleanLength))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid value", $"A value is at most {DimensionSpec.MaxCleanLength} characters.");
            }

            var attributes = (pick.Attributes ?? [])
                .Select(a => new DimensionAttributeMatch(a.Name ?? string.Empty, (a.Values ?? []).Where(v => v is not null).ToList()))
                .ToList();
            if (attributes.Any(a => a.Values.Any(v => v.Length > DimensionSpec.MaxAttributeValueLength)))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid attribute value", $"An attribute value is at most {DimensionSpec.MaxAttributeValueLength} characters.");
            }

            picks.Add(new DimensionPick(dimension, pick.ValueIds ?? [], values, attributes));
        }

        try
        {
            var set = await DimensionSearch.ComposeAsync(ledger, picks, body?.Kind, body?.Within, ct).ConfigureAwait(false);
            var request = new JsonObject { ["kind"] = set.Kind, ["query"] = set.Query, ["limit"] = 1000 }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return TypedResults.Ok(new DeliveryDimensionSearchDto(
                set.Kind, set.Query, request,
                set.Parts.Select(p => new DeliveryDimensionSearchPartDto(
                    p.DimensionId, p.Dimension, p.AggregateBy,
                    p.Values.Select(v => new DeliveryDimensionFilterValueDto(v.MemberId, v.Value, v.Records, v.RecordsExact, v.Originals)).ToList(),
                    p.Keys, p.Unfilterable, p.Filter, p.Query,
                    p.Attributes.Select(a => new DeliveryDimensionAttributePickRequest(a.Name, a.Values)).ToList())).ToList(),
                set.Clauses, set.Removed, set.Missing, set.Notes));
        }
        catch (Exception ex) when (ex is DeliveryException or OsduQueryException)
        {
            return Problem(StatusCodes.Status400BadRequest, "No search", ex.Message);
        }
    }

    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportAsync(
        int dimensionId, string? set, string? format, ILedger ledger, CancellationToken ct)
    {
        if (DimensionExport.SetOf(set) is not { } chosenSet)
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown set", $"set '{set}' is not one of values, keys, table.");
        }

        if (DimensionExport.FormatOf(format) is not { } chosenFormat)
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown format", $"format '{format}' is not one of csv, jsonl.");
        }

        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        // The export is written as it is read, a ledger page at a time, so a dimension of millions of keys is never held whole;
        // the request's own cancellation stops it when the caller goes away.
        return TypedResults.Stream(
            stream => DimensionExport.WriteAsync(ledger, dimension, chosenSet, chosenFormat, stream, ct),
            DimensionExport.MediaType(chosenFormat),
            DimensionExport.FileName(dimension, chosenSet, chosenFormat));
    }

    private static async Task<Ok<IReadOnlyList<DeliveryDimensionRunBuildDto>>> ListRunBuildsAsync(Guid runId, ILedger ledger, CancellationToken ct)
    {
        var runs = await ledger.DimensionRunsOfAsync(runId, ct).ConfigureAwait(false);
        var names = new Dictionary<int, string>();
        foreach (var id in runs.Select(r => r.DimensionId).Distinct())
        {
            names[id] = (await ledger.GetDimensionAsync(id, ct).ConfigureAwait(false))?.Name ?? id.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryDimensionRunBuildDto>>(runs.Select(r => new DeliveryDimensionRunBuildDto(names[r.DimensionId], ToDto(r))).ToList());
    }

    /// <summary>
    /// Each flow bound to the board's partition as a run binds it, with its dimensions: those it declares, then those the
    /// ledger keeps that it no longer declares, each with the build that wrote what it holds and its newest build.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryDimensionFlowDto>> BoardAsync(
        IReadOnlyList<CatalogPipeline> pipelines, string? partition, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry,
        CancellationToken ct)
    {
        var registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        var settled = new List<(CatalogPipeline Pipeline, DimensionFlowDefinition? Flow, DimensionFlowDefinition? Bound, string? Partition, string? Problem)>();
        foreach (var pipeline in pipelines)
        {
            DimensionFlowDefinition flow;
            try
            {
                flow = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
            }
            catch (FlowValidationException ex)
            {
                settled.Add((pipeline, null, null, partition, $"The catalog's copy of the flow does not parse: {ex.Message} Re-sync the repository."));
                continue;
            }

            var (bound, kept, why) = await SettleAsync(flow, partition, ledger, registered, ct).ConfigureAwait(false);
            settled.Add((pipeline, flow, bound, kept, why));
        }

        var held = new Dictionary<Guid, IReadOnlyList<DimensionState>>();
        foreach (var bound in settled.Select(s => s.Bound).OfType<DimensionFlowDefinition>())
        {
            if (!held.ContainsKey(bound.LedgerId))
            {
                held[bound.LedgerId] = await ledger.ListDimensionsAsync(null, bound.LedgerId, ct).ConfigureAwait(false);
            }
        }

        var builds = await BuildsAsync(ledger, held.Values.SelectMany(d => d).ToList(), ct).ConfigureAwait(false);
        var flows = new List<DeliveryDimensionFlowDto>(settled.Count);
        foreach (var (pipeline, flow, bound, kept, problem) in settled)
        {
            if (flow is null || bound is null)
            {
                flows.Add(new DeliveryDimensionFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, flow?.Description, flow?.Batch ?? pipeline.Batch, kept, false, flow?.Served(registered) ?? [], null,
                    flow is null ? [] : Parameters(flow), problem, [], Incremental(flow)));
                continue;
            }

            flows.Add(new DeliveryDimensionFlowDto(
                pipeline.Id, pipeline.RepoId, pipeline.Name, bound.Description, bound.Batch ?? pipeline.Batch, kept, true, bound.Served(registered), bound.LedgerId,
                Parameters(bound), null, Dimensions(bound, kept, held.GetValueOrDefault(bound.LedgerId) ?? [], builds), Incremental(bound)));
        }

        return flows;
    }

    /// <summary>
    /// A flow bound to the partition asked for, as a run binds it, with the partition its dimensions are kept in; for a flow
    /// whose partition is its header's, the flow as it is when that partition is the one its ledger is kept under (or it has
    /// not built yet, or none is asked for). Null with the reason otherwise.
    /// </summary>
    private static async Task<(DimensionFlowDefinition? Flow, string? Partition, string? Why)> SettleAsync(
        DimensionFlowDefinition flow, string? partition, ILedger ledger, RegisteredPartitions registered, CancellationToken ct)
    {
        if (!flow.Partitioned)
        {
            var kept = (await ledger.GetLedgerAsync(flow.LedgerId, ct).ConfigureAwait(false))?.Partition;
            return partition is null || kept is null || string.Equals(kept, partition, StringComparison.OrdinalIgnoreCase)
                ? (flow, kept ?? partition, null)
                : (null, kept, $"Dimension flow '{flow.Name}' builds in the partition its source.headers name, '{kept}', not '{partition}'.");
        }

        try
        {
            var bound = flow.ForRun(partition, registered);
            return (bound, bound.Partition, null);
        }
        catch (DeliveryException ex)
        {
            return (null, partition, ex.Message);
        }
    }

    /// <summary>
    /// The pipeline declaring <paramref name="dimension"/> (the active one first), the flow bound to the dimension's partition,
    /// and its declaration of the dimension; nulls for what the catalog no longer holds. When none of the flow's pipelines
    /// declares it, <c>Unreadable</c> names the file of one whose YAML could not be read, which may yet declare it.
    /// </summary>
    private static async Task<(CatalogPipeline? Pipeline, DimensionFlowDefinition? Flow, DimensionSpec? Spec, string? Unreadable)> DeclarationAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, DimensionState dimension, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Name == dimension.FlowName)
            .OrderByDescending(p => p.Active)
            .Take(20)
            .ToListAsync(ct).ConfigureAwait(false);
        string? unreadable = null;
        foreach (var pipeline in pipelines)
        {
            DimensionFlowDefinition flow;
            try
            {
                flow = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
                if (flow.Partitioned && dimension.Partition is { } partition)
                {
                    flow = flow.ForPartition(partition);
                }
            }
            catch (Exception ex) when (ex is FlowValidationException or DeliveryException)
            {
                unreadable ??= pipeline.RelativePath;
                continue;
            }

            // The pipeline declares the dimension kept here only when its ledger is the one the dimension was built under.
            if ((flow.Partitioned && flow.Partition is null) || flow.LedgerId != dimension.FlowId)
            {
                continue;
            }

            return (pipeline, flow, flow.Dimension(dimension.Name), null);
        }

        return (pipelines.FirstOrDefault(p => p.Active), null, null, unreadable);
    }

    private static List<DeliveryDimensionDto> Dimensions(DimensionFlowDefinition flow, string? partition, IReadOnlyList<DimensionState> held, BuildIndex builds)
    {
        var byName = held.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = flow.Dimensions.Select(spec => ToDto(spec, partition, byName.GetValueOrDefault(spec.Name), builds)).ToList();
        list.AddRange(held.Where(d => flow.Dimension(d.Name) is null).Select(d => ToDto(null, partition, d, builds)));
        return list;
    }

    /// <summary>The newest build of each dimension, and every build that wrote what a dimension holds, read together.</summary>
    private static async Task<BuildIndex> BuildsAsync(ILedger ledger, IReadOnlyList<DimensionState> dimensions, CancellationToken ct)
    {
        if (dimensions.Count == 0)
        {
            return new BuildIndex(new Dictionary<int, DimensionRunState>(), new Dictionary<long, DimensionRunState>());
        }

        var latest = (await ledger.LatestDimensionRunsAsync(dimensions.Select(d => d.DimensionId).ToList(), ct).ConfigureAwait(false))
            .ToDictionary(r => r.DimensionId);
        var byId = latest.Values.ToDictionary(r => r.DimensionRunId);
        var wanted = dimensions.Select(d => d.LastRunId).OfType<long>().Where(id => !byId.ContainsKey(id)).ToList();
        foreach (var run in await ledger.GetDimensionRunsAsync(wanted, ct).ConfigureAwait(false))
        {
            byId[run.DimensionRunId] = run;
        }

        return new BuildIndex(latest, byId);
    }

    private static DeliveryDimensionTotalsDto Totals(IReadOnlyList<DeliveryDimensionFlowDto> flows)
    {
        var building = flows.Where(f => f.BuildsPartition).ToList();
        var dimensions = building.SelectMany(f => f.Dimensions).Where(d => d.Declared && d.BuildsHere).ToList();
        return new DeliveryDimensionTotalsDto(
            building.Count,
            dimensions.Count,
            dimensions.Count(d => d.Current is not null),
            dimensions.Count(d => d.Current is null),
            dimensions.Count(d => d.Latest is { Status: DimensionRunStatus.Failed }),
            dimensions.Count(d => d.Latest is { Status: DimensionRunStatus.Running }),
            dimensions.Count(d => d.Changed),
            dimensions.Sum(d => d.Values),
            dimensions.Sum(d => d.Keys));
    }

    /// <summary>A dimension the explorer's builder drafted, as the dimension pages show one no build has registered yet.</summary>
    internal static DeliveryDimensionDto DraftDto(DimensionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return ToDto(spec, null, null, BuildIndex.None);
    }

    /// <summary>How a flow's builds load, as its incremental block says; null for a flow whose every build loads in full.</summary>
    private static DeliveryDimensionIncrementalDto? Incremental(DimensionFlowDefinition? flow)
        => flow?.Incremental is { } incremental ? new DeliveryDimensionIncrementalDto(incremental.LagMinutes, incremental.FullLoadAfterHours, incremental.FullLoad) : null;

    private static DeliveryDimensionDto ToDto(DimensionSpec? spec, string? partition, DimensionState? state, BuildIndex builds)
    {
        var current = state?.LastRunId is { } id && builds.ById.TryGetValue(id, out var wrote) ? wrote : null;
        var latest = state is not null && builds.Latest.TryGetValue(state.DimensionId, out var newest) && newest.DimensionRunId != current?.DimensionRunId ? newest : null;
        var steps = spec?.Clean ?? DimensionRunner.StepsOf(state?.CleanJson);
        return new DeliveryDimensionDto(
            state?.DimensionId,
            spec?.Name ?? state!.Name,
            spec?.Description ?? state?.Description,
            spec?.Kind ?? state!.Kind,
            spec?.Query ?? state?.Query,
            current?.Query ?? state?.Query,
            spec?.Path ?? state!.Path,
            spec?.Label ?? DimensionRunner.LabelOf(state?.LabelJson),
            spec?.Unlabelled,
            (spec?.Attributes ?? DimensionRunner.AttributesOf(state?.AttributesJson)).Select(a => new DeliveryDimensionAttributeSpecDto(
                a.Name, a.Steps, a.Collect, DimensionKeeping.Named(a.Keep), DimensionKeeping.Named(a.Many), DimensionKeeping.Named(a.Decode), a.MaxReferences, a.Trim)).ToList(),
            steps.Select(s => s.Describe()).ToList(),
            spec?.CountRecords ?? false,
            spec?.MaxValues ?? DimensionSpec.DefaultMaxValues,
            spec is not null,
            spec?.BuildsIn(partition) ?? false,
            spec is not null && current is not null && !string.Equals(spec.DefinitionHash, current.DefinitionHash, StringComparison.Ordinal),
            state?.Field is { } field ? new DeliveryDimensionFieldDto(field.Index, field.NestedPath, field.AggregateBy, field.Repeats) : null,
            state?.Members ?? 0,
            state?.Originals ?? 0,
            state?.LastBuiltUtc,
            current is null ? null : ToDto(current),
            latest is null ? null : ToDto(latest),
            state?.TableName is { } table ? DimensionTables.Shown(table) : null,
            spec?.KeyColumn ?? state?.KeyColumn ?? DimensionColumnNames.KeyRole,
            spec?.ValueColumn ?? state?.ValueColumn ?? DimensionColumnNames.ValueRole,
            state?.LastFullBuiltUtc);
    }

    private static DeliveryDimensionBuildDto ToDto(DimensionRunState r) => new(
        r.DimensionRunId, r.DimensionId, r.RunId, r.Actor, r.Status, r.StartedUtc, r.CompletedUtc, r.Error, r.DefinitionHash, r.Query, r.AggregateBy,
        r.Members, r.Originals, r.LeftOut, r.Unfilterable, r.Read.Records, r.Read.WithValue, r.Read.Nulls, r.Read.TooLong, r.Read.Unreadable,
        r.Read.Aggregations, r.Read.Slices, r.Read.Splits, r.Read.ScannedSlices, r.Read.ScanPages, r.Read.ScannedUnits, r.Read.CountQueries,
        r.Read.Labelled, r.Read.Unlabelled, r.Read.LabelQueries,
        DimensionRunner.KindsOf(r.Read.Templates).Select(k => new DeliveryDimensionKindDto(k.Kind, k.Records, k.Template)).ToList(),
        r.Read.Notes,
        new DeliveryDimensionChangesDto(
            r.Changes.MembersAdded, r.Changes.MembersRemoved, r.Changes.MembersRestored, r.Changes.OriginalsAdded, r.Changes.OriginalsRemoved,
            r.Changes.OriginalsMoved, r.Changes.OriginalsRestored),
        r.Read.Mode, r.Read.WindowFrom, r.Read.WindowTo, r.Read.ChangedRecords, r.Read.TouchedKeys);

    private static DeliveryDimensionValueDto ToDto(DimensionMemberState m, IReadOnlyList<DeliveryDimensionKeyBriefDto> top) => new(
        m.MemberId, m.Value, m.Records, m.RecordsExact, m.Originals, m.Unfilterable, m.Filter, m.FilterParts, m.FirstSeenRunId, m.FirstSeenUtc, m.RemovedRunId,
        m.RemovedUtc, top, m.Attributes.Select(a => new DeliveryDimensionValueAttributeDto(a.Name, a.Value, a.Keys)).ToList());

    private static DeliveryDimensionKeyDto ToDto(DimensionValueState v) => new(
        v.ValueId, v.Original, v.Label, v.LabelFrom, v.MemberId, v.MemberValue, v.LeftOut, v.Note, v.Count, v.Filterable, v.Filter, v.FirstSeenRunId,
        v.FirstSeenUtc, v.MemberSinceRunId, v.RemovedRunId, v.RemovedUtc, v.Attributes.Select(a => new DeliveryDimensionAttributeDto(a.Name, a.Value, a.From, a.Records)).ToList());

    /// <summary>
    /// The attribute matches the <c>attr</c> parameters ask for, each <c>Name:value</c> (the value everything after the first
    /// colon), named as the dimension declares them: several of one attribute are any of their values, several attributes
    /// are all of them. A problem when one is not a match, or names no attribute of the dimension.
    /// </summary>
    private static (IReadOnlyList<DimensionAttributeMatch>? Matches, ProblemHttpResult? Problem) AttributeMatches(DimensionState dimension, string[]? asked)
    {
        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        return AttributeMatches(
            asked,
            name => declared.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.Name,
            name => NoAttribute(dimension, name, declared));
    }

    /// <summary>
    /// The matches the <c>attr</c> parameters ask for, each attribute named as <paramref name="named"/> gives it; a name it
    /// gives nothing for is a problem saying what <paramref name="none"/> does.
    /// </summary>
    private static (IReadOnlyList<DimensionAttributeMatch>? Matches, ProblemHttpResult? Problem) AttributeMatches(
        string[]? asked, Func<string, string?> named, Func<string, string> none)
    {
        if (asked is null || asked.Length == 0)
        {
            return (null, null);
        }

        var matches = new List<DimensionAttributeMatch>();
        foreach (var text in asked)
        {
            var at = text?.IndexOf(':', StringComparison.Ordinal) ?? -1;
            if (text is null || at <= 0 || at == text.Length - 1)
            {
                return (null, Problem(StatusCodes.Status400BadRequest, "Invalid attribute filter", $"attr '{text}' is not Name:value."));
            }

            var name = text[..at].Trim();
            var value = text[(at + 1)..];
            if (value.Length > DimensionSpec.MaxAttributeValueLength)
            {
                return (null, Problem(StatusCodes.Status400BadRequest, "Invalid attribute filter", $"An attribute value is at most {DimensionSpec.MaxAttributeValueLength} characters."));
            }

            if (named(name) is not { } attribute)
            {
                return (null, Problem(StatusCodes.Status400BadRequest, "No such attribute", none(name)));
            }

            var index = matches.FindIndex(m => string.Equals(m.Name, attribute, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                matches[index] = matches[index] with { Values = [.. matches[index].Values, value] };
            }
            else
            {
                matches.Add(new DimensionAttributeMatch(attribute, [value]));
            }
        }

        return (matches, null);
    }

    private static string NoAttribute(DimensionState dimension, string name, IReadOnlyList<DimensionAttributeSpec> declared)
        => $"Dimension {dimension.Name} reads no attribute '{name}'{(declared.Count == 0 ? "; it reads none" : $"; it reads {string.Join(", ", declared.Select(a => a.Name))}")}.";

    private static DeliveryDimensionChangeDto ToDto(DimensionChangeState c) => new(
        c.ChangeId, c.DimensionRunId, c.ValueId, c.Original, c.Change, c.FromMemberId, c.FromValue, c.ToMemberId, c.ToValue, c.ChangedUtc);

    private static DeliveryDimensionFilterDto ToDto(DimensionFilterSet f) => new(
        f.Kind, f.Query, f.AggregateBy, f.Filters, f.Searches,
        f.Members.Select(m => new DeliveryDimensionFilterValueDto(m.MemberId, m.Value, m.Records, m.RecordsExact, m.Originals)).ToList(),
        f.Originals, f.Unfilterable, f.UnfilterableNamed, f.Removed, f.Missing);

    /// <summary>The parameters a run of the flow takes, in the order the document declares them.</summary>
    private static List<DeliveryParameterDto> Parameters(DimensionFlowDefinition flow)
        => flow.Parameters.Select(p => new DeliveryParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description)).ToList();

    private static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPage, 1, OsduLedger.MaxDimensionPage);

    private static ProblemHttpResult? SearchProblem(string? search)
        => search is { Length: > MaxSearchLength }
            ? Problem(StatusCodes.Status400BadRequest, "Search too long", $"A search is at most {MaxSearchLength} characters, the longest key a dimension keeps.")
            : null;

    /// <summary>
    /// The cursor a page of values ends at: the last value's records and text, carried opaquely so the next request hands it
    /// back as it was given.
    /// </summary>
    internal static string ValueCursor(DimensionMemberState last)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{last.Records}:{last.Value}")));

    /// <summary>The value cursor a request handed back, or null when it is not one this API gave.</summary>
    internal static DimensionMemberCursor? ValueCursorOf(string text)
    {
        if (Decoded(text) is not { } decoded)
        {
            return null;
        }

        // The records come first and hold no colon; the value is everything after the first one, colons included.
        var at = decoded.IndexOf(':', StringComparison.Ordinal);
        var value = at > 0 ? decoded[(at + 1)..] : string.Empty;
        return value.Length is > 0 and <= DimensionSpec.MaxCleanLength
            && long.TryParse(decoded.AsSpan(0, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out var records)
                ? new DimensionMemberCursor(value, records)
                : null;
    }

    /// <summary>The cursor a page of keys ends at: the last key's count and id.</summary>
    internal static string KeyCursor(DimensionValueState last)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{last.Count}:{last.ValueId}")));

    /// <summary>The key cursor a request handed back, or null when it is not one this API gave.</summary>
    internal static DimensionValueCursor? KeyCursorOf(string text)
    {
        if (Decoded(text) is not { } decoded || decoded.Split(':') is not [var count, var id])
        {
            return null;
        }

        return long.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counted)
            && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyId)
                ? new DimensionValueCursor(keyId, counted)
                : null;
    }

    private static string? Decoded(string text)
    {
        if (text.Length > 2048)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(text.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ProblemHttpResult BadCursor(string after)
        => Problem(StatusCodes.Status400BadRequest, "Invalid cursor", $"'{(after.Length > 60 ? after[..60] + "..." : after)}' is not a cursor a page of this dimension gave; start from the first page.");

    private static ProblemHttpResult NoDimension(int dimensionId)
        => Problem(StatusCodes.Status404NotFound, "Not found", string.Create(CultureInfo.InvariantCulture, $"No dimension {dimensionId}."));

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);

    private sealed record BuildIndex(IReadOnlyDictionary<int, DimensionRunState> Latest, IReadOnlyDictionary<long, DimensionRunState> ById)
    {
        /// <summary>No build at all: a dimension only drafted.</summary>
        public static BuildIndex None { get; } = new(new Dictionary<int, DimensionRunState>(), new Dictionary<long, DimensionRunState>());
    }
}
