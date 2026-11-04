using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Catalog;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// An assertion of a test as the document states it: <c>Disabled</c> says why it is disabled (null when it is judged),
/// <c>AsOf</c> the day what it expects was stated (its own or its test's), <c>ValidUntil</c> the last day that holds
/// (<c>validUntil</c>, or <c>validFor</c> after <c>asOf</c>; null for good), and <c>NeedsNewProofValues</c> is true when that
/// day is past, so a run flags the check rather than judging it.
/// </summary>
public sealed record DeliveryAssertionDefinitionDto(
    int Index, string Label, string Type, string Severity, string Expected, string? Description,
    string? Disabled = null, string? AsOf = null, string? ValidUntil = null, bool NeedsNewProofValues = false);

/// <summary>One run's outcome of a test, as a history strip shows it.</summary>
public sealed record DeliveryAssertionPointDto(long AssertionRunId, string Outcome, int FailedAssertions, long? Matched, DateTime CompletedUtc);

/// <summary>A test's latest result, without its detail.</summary>
public sealed record DeliveryAssertionLatestDto(
    long ResultId, long AssertionRunId, string Outcome, string? Severity, long? Matched, long? Evaluated, bool Sampled, int Assertions, int FailedAssertions,
    long DurationMs, string? Error, DateTime CompletedUtc, string DefinitionHash);

/// <summary>One dimension of an assertion: what it is about along one axis, such as <c>Country: Norway</c>.</summary>
public sealed record DeliveryAssertionDimensionDto(string Name, string Value);

/// <summary>
/// One test of an assertion flow as the dashboard shows it: what the document says of it, whether it fits the template of
/// its kind, and its latest result. <c>Changed</c> is true when the test's definition has changed since its latest result was
/// recorded. <c>Disabled</c>, <c>AsOf</c> and <c>ValidUntil</c> are the test's own, as a check's are; <c>NeedsNewProofValues</c>
/// is true when every check it judges is past its day. <c>TemplateMapping</c> names the mapping (<c>Name@version</c>) whose
/// pinned template the test is checked against, and <c>TemplatePinned</c> is true when the test names its template version
/// itself rather than taking the newest captured. <c>Title</c> is what people read it as, and <c>Dimensions</c> every axis the
/// dashboard maps it by: Set, Type and Check, then the ones its documents name
/// (osdu/docs/reference/flow/assertion-sets.md, Dimensions).
/// </summary>
public sealed record DeliveryAssertionTestDto(
    string Name, string? Title, string? Description, string Kind, IReadOnlyList<string> Tags, string Severity, string? Query, int Ids, bool Bulk,
    string Read, int MaxRecords, IReadOnlyList<DeliveryAssertionDefinitionDto> Assertions, bool RunsHere, IReadOnlyList<string> Partitions,
    string? Template, IReadOnlyList<string> Problems, DeliveryAssertionLatestDto? Latest, bool Changed, string DefinitionHash,
    IReadOnlyList<DeliveryAssertionDimensionDto> Dimensions, string? Disabled = null, string? AsOf = null, string? ValidUntil = null,
    bool NeedsNewProofValues = false, string? TemplateMapping = null, bool TemplatePinned = false);

/// <summary>An assertion run as listings show it.</summary>
public sealed record DeliveryAssertionRunDto(
    long AssertionRunId, Guid FlowId, string FlowName, string? Partition, Guid? RunId, string Actor, string? Selection, string Status,
    int Tests, int Passed, int Failed, int Warned, int Errored, int Skipped, DateTime StartedUtc, DateTime? CompletedUtc, string? Error);

/// <summary>
/// One assertion flow on a board, in the partition the board is read in: its tests, the partitions it tests and whether the
/// board's partition is one, the parameters a run of it takes, its last run, and what keeps the flow from being shown (a
/// document the catalog cannot parse, a partition it does not test). <c>Parses</c> tells the two apart: false when the
/// catalog's copy of the document does not parse, which needs a fix, where a flow of another partition needs none.
/// <c>Set</c> is the set its tests are grouped under: its <c>set</c>, else its batch; null when it names neither. <c>Title</c>
/// is what people read the flow as.
/// </summary>
public sealed record DeliveryAssertionFlowDto(
    Guid PipelineId, Guid RepoId, string Name, string? Title, string? Description, string? Batch, string? Set, string? Partition, bool TestsPartition, bool Parses,
    IReadOnlyList<string> Partitions, string FailRunOn, IReadOnlyList<DeliveryParameterDto> Parameters, string? Problem, DeliveryAssertionRunDto? LastRun,
    IReadOnlyList<DeliveryAssertionTestDto> Tests);

/// <summary>
/// What a board adds up to, over every test it shows that runs in its partition. A test its document disables counts as
/// <c>Disabled</c> alone, whatever an earlier run found, and the pass rate is a share of the tests a run judged.
/// </summary>
public sealed record DeliveryAssertionTotalsDto(
    int Flows, int Tests, int Passed, int Failed, int Warned, int Errored, int NotRun, int Disabled, int Problems, int Changed, double? PassRate);

/// <summary>The board of every assertion flow, or of one, in a partition.</summary>
public sealed record DeliveryAssertionBoardDto(string? Partition, DeliveryAssertionTotalsDto Totals, IReadOnlyList<DeliveryAssertionFlowDto> Flows);

/// <summary>A test's result whole, by the id the board gave it (<c>Latest.ResultId</c>), with the report it is from.</summary>
public sealed record DeliveryAssertionResultDto(long ResultId, long AssertionRunId, TestResult Result);

/// <summary>An assertion run with every test's full result.</summary>
public sealed record DeliveryAssertionRunDetailDto(DeliveryAssertionRunDto Run, Guid? PipelineId, IReadOnlyList<TestResult> Results);

/// <summary>One test's row in a flow's history matrix: its outcome in each run of the matrix, or null where the run did not run it.</summary>
public sealed record DeliveryAssertionMatrixRowDto(string Test, string Kind, IReadOnlyList<DeliveryAssertionPointDto?> Cells);

/// <summary>A flow's recent runs against its tests: the runs, newest first, and each test's outcome in each.</summary>
public sealed record DeliveryAssertionMatrixDto(IReadOnlyList<DeliveryAssertionRunDto> Runs, IReadOnlyList<DeliveryAssertionMatrixRowDto> Tests);

/// <summary>One test's results over its recent runs, newest first, each whole: what every assertion found, run by run.</summary>
public sealed record DeliveryAssertionTestHistoryDto(string Test, IReadOnlyList<TestResult> Results);

/// <summary>
/// The T-SQL of the staged rows one check of a run read (osdu/docs/reference/flow/staged-accounting.md, The rows in the
/// source): the test, the check (its position in the test, from 0), which rows (<c>listed</c>, the rows the check lists by
/// their keys, the default; or <c>all</c>, every row of the scope it read), and for every row the most rows a statement
/// returns (0, the default, for all of them).
/// </summary>
public sealed record DeliveryAssertionSourceSqlRequest(string? Test, int? Assertion, string? Rows, long? MaxRows);

/// <summary>
/// The report of assertion flows (osdu/docs/reference/flow/assertion.md, The report): the dashboard's data across every
/// assertion flow, of one set and of one flow, tests' latest results whole by id, a flow's runs, a run with every result, the
/// matrix of tests against runs, one test's trend, the report of a run as JSON, Markdown, HTML or JUnit XML, and a set's as
/// JSON, Markdown or JUnit XML. Every read is in a partition: the one the request names, else the workbench's, else the one a
/// run of the flow would test. Running tests is a run like any other, queued through the platform's trigger.
/// </summary>
public static class DeliveryAssertionEndpoints
{
    /// <summary>The most assertion flows one board shows.</summary>
    private const int MaxFlows = 500;

    /// <summary>The most results one read of results whole names.</summary>
    private const int MaxResults = 100;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/assertions", GetBoardAsync).WithName("GetDeliveryAssertionBoard");
        delivery.MapGet("/flows/{pipelineId:guid}/assertions", GetFlowBoardAsync).WithName("GetDeliveryAssertionFlowBoard");
        delivery.MapGet("/flows/{pipelineId:guid}/assertion-runs", ListRunsAsync).WithName("ListDeliveryAssertionRuns");
        delivery.MapGet("/flows/{pipelineId:guid}/assertion-matrix", GetMatrixAsync).WithName("GetDeliveryAssertionMatrix");
        delivery.MapGet("/flows/{pipelineId:guid}/assertions/{test}/history", GetTestHistoryAsync).WithName("GetDeliveryAssertionTestHistory");
        delivery.MapGet("/assertion-runs/{assertionRunId:long}", GetRunAsync).WithName("GetDeliveryAssertionRun");
        delivery.MapGet("/assertion-runs/{assertionRunId:long}/report", GetReportAsync).WithName("GetDeliveryAssertionReport");
        delivery.MapGet("/assertion-results", GetResultsAsync).WithName("GetDeliveryAssertionResults");
        delivery.MapGet("/assertion-sets/{set}/report", GetSetReportAsync).WithName("GetDeliveryAssertionSetReport");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/assertion-runs/{assertionRunId:long}/source-sql", SourceSqlAsync).WithName("WriteDeliveryAssertionSourceSql");
    }

    /// <summary>
    /// The staged rows a check of a run read, as T-SQL over its delivery flow's own tables: what the source holds of the rows it
    /// listed, or of every row of the scope it read. The rows are the ones the result names, read under the values the run read
    /// them under, and the script is written by the same writer as a mapping's source query (<see cref="SourceSqlOperation"/>),
    /// in this process: no row is read and nothing is run. The delivery flow is the one of that name in the repository the
    /// assertion flow is synced from, bound to the partition the run tested.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> SourceSqlAsync(
        long assertionRunId, DeliveryAssertionSourceSqlRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request?.Test?.Trim() is not { Length: > 0 } test || request.Assertion is not { } index)
        {
            return DeliveryEndpoints.Invalid("A query of a check's rows names its test (test) and the check (assertion: its position in the test, from 0).");
        }

        bool? listed = request.Rows?.Trim() switch
        {
            null or "" or "listed" => true,
            "all" => false,
            _ => null,
        };
        if (listed is null)
        {
            return DeliveryEndpoints.Invalid($"rows is listed (the rows the check lists) or all (every row of the scope it read), not '{request.Rows}'.");
        }

        if (request.MaxRows is < 0)
        {
            return DeliveryEndpoints.Invalid($"A query returns a number of rows, or 0 for all of them; {request.MaxRows.Value.ToString(CultureInfo.InvariantCulture)} is neither.");
        }

        var run = await ledger.GetAssertionRunAsync(assertionRunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return TypedResults.Problem(detail: $"No assertion run {assertionRunId}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var kept = (await ledger.ListAssertionResultsAsync(assertionRunId, ct).ConfigureAwait(false))
            .FirstOrDefault(r => string.Equals(r.TestName, test, StringComparison.OrdinalIgnoreCase));
        var check = TestResults.Deserialize(kept?.Detail)?.Assertions.FirstOrDefault(a => a.Index == index);
        if (check is null)
        {
            return TypedResults.Problem(
                detail: kept is null
                    ? $"Assertion run {assertionRunId} of '{run.FlowName}' holds no result of a test '{test}'."
                    : $"The result of '{kept.TestName}' in assertion run {assertionRunId} has no check {index.ToString(CultureInfo.InvariantCulture)}.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (check.Staged is not { } staged)
        {
            return TypedResults.Problem(
                detail: $"'{check.Label}' reads no delivery flow's staged rows, so there are none to query; a delivered assertion with staged: true reads them.",
                statusCode: StatusCodes.Status409Conflict, title: "No staged rows");
        }

        var (flow, problem) = await StagedFlowAsync(db, documents, partitions, run, staged, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        IReadOnlyList<string> keys = listed.Value
            ? DeliveryValueCheckEndpoints.FittingKeys(check.Examples.Select(e => e.Key).OfType<IReadOnlyList<string>>(), flow.Flow.Source.Record.Key.Count)
            : [];
        if (listed.Value && keys.Count == 0)
        {
            return TypedResults.Problem(
                detail: $"'{check.Label}' lists no staged row by its key in the record table of '{flow.Flow.Label}' as it is now; ask for every row of the scope it read (rows: all).",
                statusCode: StatusCodes.Status409Conflict, title: "No rows listed");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var sql = new DeliverySourceSqlRequest(null, staged.Values, listed.Value ? null : request.MaxRows ?? 0, keys);
        if (DeliveryValueCheckEndpoints.SourceSqlArguments(flow.Flow, sql, arguments) is { } refused)
        {
            // The request was well formed: the delivery flow changed since the run read its rows under these values.
            return TypedResults.Problem(
                detail: $"The rows '{check.Label}' read cannot be queried in '{flow.Flow.Label}' as it is now: {refused}",
                statusCode: StatusCodes.Status409Conflict, title: "Delivery flow changed");
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, SourceSqlOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The delivery flow whose rows a check read: the active delivery flow of that name in the repository the run's assertion
    /// flow is synced from, bound to the partition the run tested and the interface it read. Null with the problem to answer with.
    /// </summary>
    private static async Task<(DeliveryEndpoints.FlowContext? Flow, ProblemHttpResult? Problem)> StagedFlowAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, AssertionRunState run, StagedScope staged, CancellationToken ct)
    {
        var repoId = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == AssertionFlowDefinition.FlowTypeName && p.Name == run.FlowName)
            .OrderByDescending(p => p.Active)
            .Select(p => (Guid?)p.RepoId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repoId is null)
        {
            return (null, TypedResults.Problem(
                detail: $"The catalog holds no assertion flow '{run.FlowName}', so the delivery flow '{staged.Flow}' beside it cannot be found; sync the repository that declares it.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found"));
        }

        var pipelineId = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Kind == FlowDefinition.FlowTypeName && p.Active && p.Name == staged.Flow)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipelineId is null)
        {
            return (null, TypedResults.Problem(
                detail: $"Delivery flow '{staged.Flow}', whose rows the check read, is not an active flow of the repository '{run.FlowName}' is synced from; sync the repository, then ask again.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found"));
        }

        return await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId.Value, staged.Interface, run.Partition, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every active assertion flow of the catalog, each in the partition the request reads; with <paramref name="set"/>, the
    /// flows of that set alone.
    /// </summary>
    private static async Task<Results<Ok<DeliveryAssertionBoardDto>, ProblemHttpResult>> GetBoardAsync(
        string? partition, string? set, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, IRepositoryFolders folders, TimeProvider time, CancellationToken ct)
    {
        if (set is not null && !SelectableNames.IsName(set))
        {
            return SetNameProblem(set);
        }

        var named = WorkbenchPartition.Named(partition, request);
        var parsed = await ParseAsync(await ActiveFlowsAsync(db, ct).ConfigureAwait(false), named, documents, ledger, registry, ct).ConfigureAwait(false);
        var flows = await BoardAsync(set is null ? parsed : InSet(parsed, set), named, ledger, registry, folders, documents, time.GetUtcNow(), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryAssertionBoardDto(named, Totals(flows), flows));
    }

    /// <summary>
    /// The results the comma-separated <paramref name="ids"/> name (at most <see cref="MaxResults"/>), each whole: what the
    /// dashboard opens an assertion on, and what its download carries of the assertions that need a look.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryAssertionResultDto>>, ProblemHttpResult>> GetResultsAsync(
        string? ids, ILedger ledger, CancellationToken ct)
    {
        var named = new List<long>();
        foreach (var part in (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return TypedResults.Problem(detail: $"'{part}' is not a result id.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid result id");
            }

            named.Add(id);
        }

        if (named.Count == 0 || named.Count > MaxResults)
        {
            return TypedResults.Problem(
                detail: $"ids names {named.Count} results; name between 1 and {MaxResults}, comma-separated.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid result ids");
        }

        var results = await ledger.GetAssertionResultsAsync(named, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAssertionResultDto>>(results
            .Select(r => (State: r, Result: TestResults.Deserialize(r.Detail)))
            .Where(r => r.Result is not null)
            .Select(r => new DeliveryAssertionResultDto(r.State.ResultId, r.State.AssertionRunId, r.Result!))
            .ToList());
    }

    /// <summary>
    /// A set's report in the partition the request reads, as JSON, Markdown or JUnit XML, for a pull request, a wiki or a CI
    /// server. Its dashboard, the page to present, downloads from the GUI.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetSetReportAsync(
        string set, string? partition, string? format, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, TimeProvider time, CancellationToken ct)
    {
        if (AssertionReport.FormatOf(format) is not { } chosen || chosen == ReportFormat.Html)
        {
            return TypedResults.Problem(
                detail: $"format '{format}' is not one of json, md, junit for a set; the set's dashboard downloads as a page from the Assertions page.",
                statusCode: StatusCodes.Status400BadRequest, title: "Unknown report format");
        }

        var named = WorkbenchPartition.Named(partition, request);
        var (members, problem) = await SetMembersAsync(set, named, db, documents, ledger, registry, ct).ConfigureAwait(false);
        if (members is null)
        {
            return problem!;
        }

        var report = await ReadSetAsync(set, named, members, ledger, time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var (mediaType, _) = AssertionReport.Describe(chosen);
        return TypedResults.File(Encoding.UTF8.GetBytes(report.Render(chosen)), mediaType, report.FileName(chosen));
    }

    /// <summary>The active assertion flows of the catalog, by name, as many as one board shows.</summary>
    private static Task<List<CatalogPipeline>> ActiveFlowsAsync(CatalogDbContext db, CancellationToken ct)
        => db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == AssertionFlowDefinition.FlowTypeName && p.Active)
            .OrderBy(p => p.Name)
            .Take(MaxFlows)
            .ToListAsync(ct);

    /// <summary>The flows of a set, by its name as a flow names it (ignoring case); a flow whose document does not parse is in its batch's.</summary>
    private static List<ParsedFlow> InSet(IReadOnlyList<ParsedFlow> parsed, string set)
        => parsed.Where(p => string.Equals(SetOf(p), set, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The set a flow is listed in: its document's, or for a document that does not parse, its pipeline's batch.</summary>
    private static string? SetOf(ParsedFlow parsed) => parsed.Flow is { } flow ? flow.Set : parsed.Pipeline.Batch;

    /// <summary>The flows of a set in the partition, parsed and bound; null with the problem to answer with when the name is no set's.</summary>
    private static async Task<(List<ParsedFlow>? Members, ProblemHttpResult? Problem)> SetMembersAsync(
        string set, string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry, CancellationToken ct)
    {
        if (!SelectableNames.IsName(set))
        {
            return (null, SetNameProblem(set));
        }

        var members = InSet(await ParseAsync(await ActiveFlowsAsync(db, ct).ConfigureAwait(false), partition, documents, ledger, registry, ct).ConfigureAwait(false), set);
        return members.Count > 0
            ? (members, null)
            : (null, TypedResults.Problem(
                detail: $"No assertion flow is in set '{set}': a flow names its set with set:, and one that names none is in its batch's.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found"));
    }

    private static ProblemHttpResult SetNameProblem(string set)
        => TypedResults.Problem(detail: $"'{set}' is not a set name: {SelectableNames.Rule}.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid set name");

    /// <summary>
    /// A set's report from its flows, under the set's name as its flows write it: each bound flow with the partition its tests
    /// are judged in, each other with its problem.
    /// </summary>
    private static async Task<AssertionSetReport> ReadSetAsync(
        string set, string? partition, IReadOnlyList<ParsedFlow> members, ILedger ledger, DateTime generatedUtc, CancellationToken ct)
    {
        var bound = new List<AssertionSetMember>(members.Count);
        foreach (var member in members)
        {
            bound.Add(member.Bound is null
                ? new AssertionSetMember(member.Pipeline.Name, null, null, member.Problem)
                : new AssertionSetMember(member.Pipeline.Name, member.Bound, await TestPartitionAsync(member.Bound, ledger, ct).ConfigureAwait(false), null));
        }

        var named = members.Select(SetOf).FirstOrDefault(s => s is not null) ?? set;
        return await AssertionSetReport.ReadAsync(named, partition, bound, ledger, generatedUtc, ct).ConfigureAwait(false);
    }

    /// <summary>The partition a bound flow's tests are judged in: the one it is bound to, else the one its ledger is kept under.</summary>
    private static async Task<string?> TestPartitionAsync(AssertionFlowDefinition bound, ILedger ledger, CancellationToken ct)
        => bound.Partition ?? (await ledger.GetLedgerAsync(bound.LedgerId, ct).ConfigureAwait(false))?.Partition;

    /// <summary>One assertion flow's board.</summary>
    private static async Task<Results<Ok<DeliveryAssertionBoardDto>, ProblemHttpResult>> GetFlowBoardAsync(
        Guid pipelineId, string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, IRepositoryFolders folders, TimeProvider time, CancellationToken ct)
    {
        var (pipeline, problem) = await PipelineAsync(db, pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return problem!;
        }

        var named = WorkbenchPartition.Named(partition, request);
        var parsed = await ParseAsync([pipeline], named, documents, ledger, registry, ct).ConfigureAwait(false);
        var flows = await BoardAsync(parsed, named, ledger, registry, folders, documents, time.GetUtcNow(), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryAssertionBoardDto(flows[0].Partition ?? named, Totals(flows), flows));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAssertionRunDto>>, ProblemHttpResult>> ListRunsAsync(
        Guid pipelineId, string? partition, int? max, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        var (bound, problem) = await BindAsync(db, documents, ledger, registry, pipelineId, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (bound is null)
        {
            return problem!;
        }

        var runs = await ledger.ListAssertionRunsAsync(bound.LedgerId, Math.Clamp(max ?? 50, 1, 500), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAssertionRunDto>>(runs.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliveryAssertionMatrixDto>, ProblemHttpResult>> GetMatrixAsync(
        Guid pipelineId, string? partition, int? runs, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        var (bound, problem) = await BindAsync(db, documents, ledger, registry, pipelineId, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (bound is null)
        {
            return problem!;
        }

        var take = Math.Clamp(runs ?? 30, 1, 200);
        var recent = await ledger.ListAssertionRunsAsync(bound.LedgerId, take, ct).ConfigureAwait(false);
        var results = await ledger.AssertionHistoryAsync(bound.LedgerId, null, take, withDetail: false, ct).ConfigureAwait(false);
        var byTest = results.GroupBy(r => r.TestName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var rows = bound.Tests
            .Select(t => t.Name)
            .Concat(byTest.Keys.Where(k => bound.Test(k) is null).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            .Select(name => new DeliveryAssertionMatrixRowDto(
                name,
                bound.Test(name)?.Kind ?? (byTest.TryGetValue(name, out var seen) ? seen[0].Kind : string.Empty),
                recent.Select(run => byTest.TryGetValue(name, out var list) && list.FirstOrDefault(r => r.AssertionRunId == run.AssertionRunId) is { } hit
                    ? Point(hit)
                    : null).ToList()))
            .ToList();
        return TypedResults.Ok(new DeliveryAssertionMatrixDto(recent.Select(ToDto).ToList(), rows));
    }

    private static async Task<Results<Ok<DeliveryAssertionTestHistoryDto>, ProblemHttpResult>> GetTestHistoryAsync(
        Guid pipelineId, string test, string? partition, int? runs, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents,
        ILedger ledger, IPartitionRegistry registry, CancellationToken ct)
    {
        var (bound, problem) = await BindAsync(db, documents, ledger, registry, pipelineId, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (bound is null)
        {
            return problem!;
        }

        if (!SelectableNames.IsName(test))
        {
            return TypedResults.Problem(detail: $"'{test}' is not a test name: {SelectableNames.Rule}.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid test name");
        }

        var results = await ledger.AssertionHistoryAsync(bound.LedgerId, test, Math.Clamp(runs ?? 30, 1, 200), withDetail: true, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryAssertionTestHistoryDto(
            bound.Test(test)?.Name ?? test,
            results.Select(r => TestResults.Deserialize(r.Detail)).Where(r => r is not null).Cast<TestResult>().ToList()));
    }

    private static async Task<Results<Ok<DeliveryAssertionRunDetailDto>, ProblemHttpResult>> GetRunAsync(
        long assertionRunId, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var run = await ledger.GetAssertionRunAsync(assertionRunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return TypedResults.Problem(detail: $"No assertion run {assertionRunId}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var results = await ledger.ListAssertionResultsAsync(assertionRunId, ct).ConfigureAwait(false);
        var pipelineId = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == AssertionFlowDefinition.FlowTypeName && p.Name == run.FlowName)
            .OrderByDescending(p => p.Active)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryAssertionRunDetailDto(ToDto(run), pipelineId, Parsed(results)));
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetReportAsync(
        long assertionRunId, string? format, ILedger ledger, TimeProvider time, CancellationToken ct)
    {
        if (AssertionReport.FormatOf(format) is not { } chosen)
        {
            return TypedResults.Problem(detail: $"format '{format}' is not one of json, md, html, junit.", statusCode: StatusCodes.Status400BadRequest, title: "Unknown report format");
        }

        var run = await ledger.GetAssertionRunAsync(assertionRunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return TypedResults.Problem(detail: $"No assertion run {assertionRunId}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var results = await ledger.ListAssertionResultsAsync(assertionRunId, ct).ConfigureAwait(false);
        var report = new AssertionReport(run, Parsed(results), time.GetUtcNow().UtcDateTime);
        var (mediaType, _) = AssertionReport.Describe(chosen);
        return TypedResults.File(Encoding.UTF8.GetBytes(report.Render(chosen)), mediaType, report.FileName(chosen));
    }

    /// <summary>The results a run recorded, read back whole; a row whose detail is not a result is left out.</summary>
    private static IReadOnlyList<TestResult> Parsed(IReadOnlyList<AssertionResultState> results)
        => results.Select(r => TestResults.Deserialize(r.Detail)).Where(r => r is not null).Cast<TestResult>().ToList();

    private static async Task<(CatalogPipeline? Pipeline, ProblemHttpResult? Problem)> PipelineAsync(CatalogDbContext db, Guid pipelineId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return (null, TypedResults.Problem(detail: $"No pipeline '{pipelineId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found"));
        }

        return string.Equals(pipeline.Kind, AssertionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase)
            ? (pipeline, null)
            : (null, TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not an assertion flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not an assertion flow"));
    }

    /// <summary>
    /// The assertion flow of a pipeline bound to the partition a request reads (osdu/docs/reference/concepts/partitions.md,
    /// In the GUI and the API), settled as a run settles it; a flow whose partition is its header's is taken as it is. Null
    /// with the problem to answer with.
    /// </summary>
    private static async Task<(AssertionFlowDefinition? Flow, ProblemHttpResult? Problem)> BindAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry, Guid pipelineId, string? partition, CancellationToken ct)
    {
        var (pipeline, problem) = await PipelineAsync(db, pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return (null, problem);
        }

        AssertionFlowDefinition flow;
        try
        {
            flow = documents.ParseAssertion(pipeline.Yaml, pipeline.RelativePath);
        }
        catch (FlowValidationException ex)
        {
            return (null, TypedResults.Problem(
                detail: $"The catalog's copy of '{pipeline.Name}' does not parse: {ex.Message} Re-sync the repository.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow document invalid"));
        }

        var (bound, why) = await SettleAsync(flow, partition, ledger, registry, ct).ConfigureAwait(false);
        return bound is null
            ? (null, TypedResults.Problem(detail: why, statusCode: StatusCodes.Status400BadRequest, title: flow.Partitioned && partition is null ? "Partition required" : "No such partition"))
            : (bound, null);
    }

    /// <summary>
    /// A flow bound to the partition asked for, as a run binds it; for a flow whose partition is its header's, the flow as it
    /// is when that partition is the one its ledger is kept under (or none is asked for). Null with the reason otherwise.
    /// </summary>
    private static async Task<(AssertionFlowDefinition? Flow, string? Why)> SettleAsync(
        AssertionFlowDefinition flow, string? partition, ILedger ledger, IPartitionRegistry registry, CancellationToken ct)
    {
        if (!flow.Partitioned)
        {
            if (partition is null)
            {
                return (flow, null);
            }

            var kept = (await ledger.GetLedgerAsync(flow.LedgerId, ct).ConfigureAwait(false))?.Partition;
            return kept is null || string.Equals(kept, partition, StringComparison.OrdinalIgnoreCase)
                ? (flow, null)
                : (null, $"Assertion flow '{flow.Name}' tests the partition its source.headers name, '{kept}', not '{partition}'.");
        }

        try
        {
            var registered = flow.NeedsRegistry(partition) ? await registry.ReadAsync(ct).ConfigureAwait(false) : RegisteredPartitions.None;
            return (flow.ForRun(partition, registered), null);
        }
        catch (DeliveryException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// One check of a test as the board shows it: what it asserts, and whether it is disabled or past the day its values hold
    /// until, as a run would find it now.
    /// </summary>
    private static DeliveryAssertionDefinitionDto Definition(int index, TestAssertion assertion, AssertionTest test, DateTimeOffset now)
    {
        var (asOf, validUntil, validFor) = AssertionActivity.Validity(assertion, test);
        return new DeliveryAssertionDefinitionDto(
            index, assertion.Label, assertion.Type, AssertionText.Of(assertion.Severity), assertion.Expected, assertion.Description,
            assertion.Disabled, asOf, LastDay(asOf, validUntil, validFor), AssertionActivity.Stale(assertion, test, now) is not null);
    }

    /// <summary>The last day values hold (<c>2027-01-11</c>), from <c>validUntil</c> or <c>validFor</c> after <c>asOf</c>; null when they hold for good.</summary>
    private static string? LastDay(string? asOf, string? validUntil, string? validFor)
        => AssertionActivity.Last(asOf, validUntil, validFor) is { } last
            ? last.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// An assertion flow of the catalog as a board reads it: its document parsed, and bound to the board's partition; or the
    /// problem that keeps it from being shown there (a document that does not parse, with no <c>Flow</c>, or a partition it
    /// does not test, with no <c>Bound</c>).
    /// </summary>
    private sealed record ParsedFlow(CatalogPipeline Pipeline, AssertionFlowDefinition? Flow, AssertionFlowDefinition? Bound, string? Problem);

    private static async Task<List<ParsedFlow>> ParseAsync(
        IReadOnlyList<CatalogPipeline> pipelines, string? partition, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry,
        CancellationToken ct)
    {
        var parsed = new List<ParsedFlow>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            try
            {
                var flow = documents.ParseAssertion(pipeline.Yaml, pipeline.RelativePath);
                var (bound, why) = await SettleAsync(flow, partition, ledger, registry, ct).ConfigureAwait(false);
                parsed.Add(new ParsedFlow(pipeline, flow, bound, why));
            }
            catch (FlowValidationException ex)
            {
                parsed.Add(new ParsedFlow(pipeline, null, null, $"The catalog's copy of the flow does not parse: {ex.Message}"));
            }
        }

        return parsed;
    }

    private static async Task<IReadOnlyList<DeliveryAssertionFlowDto>> BoardAsync(
        IReadOnlyList<ParsedFlow> parsed, string? partition, ILedger ledger, IPartitionRegistry registry, IRepositoryFolders repositories,
        DeliveryDocumentLoader documents, DateTimeOffset now, CancellationToken ct)
    {
        var registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        var ledgerIds = parsed.Where(p => p.Bound is not null).Select(p => p.Bound!.LedgerId).ToList();
        var latest = (await ledger.LatestAssertionResultsAsync(ledgerIds, ct: ct).ConfigureAwait(false))
            .GroupBy(r => r.FlowId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.TestName, StringComparer.OrdinalIgnoreCase));
        var flows = new List<DeliveryAssertionFlowDto>(parsed.Count);
        foreach (var entry in parsed)
        {
            var (pipeline, flow, bound, problem) = entry;
            if (flow is null || bound is null)
            {
                flows.Add(new DeliveryAssertionFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, flow?.Title, flow?.Description, flow?.Batch ?? pipeline.Batch, SetOf(entry), partition, false, flow is not null,
                    flow?.Served(registered) ?? [], FailRunOnText(flow?.FailRunOn ?? FailRunOn.Error), flow is null ? [] : Parameters(flow), problem, null, []));
                continue;
            }

            var testPartition = await TestPartitionAsync(bound, ledger, ct).ConfigureAwait(false);
            // Each test's template as the flow reads it: from the template folders its repository's last sync recorded on the
            // flow's path, as its run reads them from the files.
            var folders = await SyncedTemplates.ReadAsync(ledger, repositories, pipeline.RepoId, ct).ConfigureAwait(false);
            MappingReader mappings = (reference, token) => SyncedMappings.ByReferenceAsync(ledger, repositories, documents, reference, token);
            var bindings = await AssertionTemplates.BindMappingsAsync(bound.Tests, mappings, ct).ConfigureAwait(false);
            var fits = await AssertionTemplates.FitAsync(bindings, folders, pipeline.RelativePath, mappings, ct).ConfigureAwait(false);
            var runs = await ledger.ListAssertionRunsAsync(bound.LedgerId, 1, ct).ConfigureAwait(false);
            var lastRun = runs.Count > 0 ? runs[0] : null;
            var ofFlow = latest.TryGetValue(bound.LedgerId, out var found) ? found : new Dictionary<string, AssertionResultState>(StringComparer.OrdinalIgnoreCase);
            var tests = bound.Tests.Select(test =>
            {
                var result = ofFlow.TryGetValue(test.Name, out var hit) ? hit : null;
                var fit = fits.TryGetValue(test.Name, out var f) ? f : TemplateFit.NotNeeded;
                return new DeliveryAssertionTestDto(
                    test.Name, test.Title, test.Description, test.Kind, test.Tags, AssertionText.Of(test.Severity), test.Query, test.Ids.Count, test.Bulk is not null,
                    test.Read == AssertionRead.Index ? "index" : "storage", test.MaxRecords,
                    test.Assertions.Select((a, i) => Definition(i, a, test, now)).ToList(),
                    test.RunsIn(testPartition), test.Partitions, fit.Version, fit.Problems,
                    result is null ? null : new DeliveryAssertionLatestDto(
                        result.ResultId, result.AssertionRunId, result.Outcome, result.Severity, result.Matched, result.Evaluated, result.Sampled, result.Assertions,
                        result.FailedAssertions, result.DurationMs, result.Error, result.CompletedUtc, result.DefinitionHash),
                    result is not null && !string.Equals(result.DefinitionHash, test.DefinitionHash, StringComparison.Ordinal),
                    test.DefinitionHash,
                    AssertionDimensions.Of(bound, test).Select(d => new DeliveryAssertionDimensionDto(d.Name, d.Value)).ToList(),
                    test.Disabled,
                    test.AsOf,
                    LastDay(test.AsOf, test.ValidUntil, test.ValidFor),
                    AssertionActivity.Stale(test, now),
                    test.TemplateMapping,
                    test.Template is not null);
            }).ToList();
            flows.Add(new DeliveryAssertionFlowDto(
                pipeline.Id, pipeline.RepoId, pipeline.Name, bound.Title, bound.Description, bound.Batch, bound.Set, testPartition, true, true, bound.Served(registered),
                FailRunOnText(bound.FailRunOn), Parameters(bound), null, lastRun is null ? null : ToDto(lastRun), tests));
        }

        return flows;
    }

    /// <summary>
    /// Whether a test's document switches it off now: the test, or every check of it, is disabled. It is then counted as
    /// disabled alone, whatever an earlier run found, as a set's report counts it.
    /// </summary>
    private static bool IsDisabled(DeliveryAssertionTestDto test)
        => test.Disabled is not null || (test.Assertions.Count > 0 && test.Assertions.All(a => a.Disabled is not null));

    /// <summary>
    /// What a board adds up to: each test by where it stands now. A disabled test counts as disabled; a test whose latest
    /// result judged an earlier version of it counts as not run, since that result says nothing of the test as it is.
    /// </summary>
    private static DeliveryAssertionTotalsDto Totals(IReadOnlyList<DeliveryAssertionFlowDto> flows)
    {
        var tests = flows.SelectMany(f => f.Tests).Where(t => t.RunsHere).ToList();
        var enabled = tests.Where(t => !IsDisabled(t)).ToList();
        var judged = enabled.Where(t => t.Latest is not null && !t.Changed).ToList();
        int Count(string outcome) => judged.Count(t => t.Latest!.Outcome == outcome);
        var passed = Count(TestOutcomes.Passed);
        var evaluated = judged.Count(t => t.Latest!.Outcome != TestOutcomes.Skipped);
        return new DeliveryAssertionTotalsDto(
            flows.Count, tests.Count, passed, Count(TestOutcomes.Failed), Count(TestOutcomes.Warned), Count(TestOutcomes.Errored),
            enabled.Count - judged.Count, tests.Count - enabled.Count, tests.Count(t => t.Problems.Count > 0), enabled.Count(t => t.Changed),
            evaluated == 0 ? null : Math.Round(100.0 * passed / evaluated, 1));
    }

    private static DeliveryAssertionPointDto Point(AssertionResultState r)
        => new(r.AssertionRunId, r.Outcome, r.FailedAssertions, r.Matched, r.CompletedUtc);

    private static DeliveryAssertionRunDto ToDto(AssertionRunState r) => new(
        r.AssertionRunId, r.FlowId, r.FlowName, r.Partition, r.RunId, r.Actor, r.Selection, r.Status,
        r.Counts.Tests, r.Counts.Passed, r.Counts.Failed, r.Counts.Warned, r.Counts.Errored, r.Counts.Skipped, r.StartedUtc, r.CompletedUtc, r.Error);

    /// <summary>The parameters a run of the flow takes, in the order the document declares them.</summary>
    private static List<DeliveryParameterDto> Parameters(AssertionFlowDefinition flow)
        => flow.Parameters.Select(p => new DeliveryParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description)).ToList();

    private static string FailRunOnText(FailRunOn failRunOn) => failRunOn switch
    {
        FailRunOn.Warning => "warning",
        FailRunOn.Never => "never",
        _ => "error",
    };
}
