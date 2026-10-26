using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>One kind a dimension's pattern matched in the partition, with its records and the template it was read against.</summary>
public sealed record DimensionKind(string Kind, long Records, string? Template);

/// <summary>What a build changed of a dimension: its values that arrived, left or came back, and its keys that arrived, left, moved or came back.</summary>
public sealed record DimensionBuildChanges(
    long ValuesAdded, long ValuesRemoved, long ValuesRestored, long KeysAdded, long KeysRemoved, long KeysMoved, long KeysRestored)
{
    public static DimensionBuildChanges None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>The changes as the ledger counts them, where a value is a member and a key an original.</summary>
    public static DimensionBuildChanges Of(DimensionChangeCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return new(
            counts.MembersAdded, counts.MembersRemoved, counts.MembersRestored, counts.OriginalsAdded, counts.OriginalsRemoved, counts.OriginalsMoved,
            counts.OriginalsRestored);
    }
}

/// <summary>
/// One dimension as a build left it: the values and keys it holds, the keys of no value and those no query can carry, the
/// keys a label was read for, what it changed, how many requests it made, and why it failed when it did.
/// </summary>
public sealed record DimensionBuildSummary(
    string Dimension,
    string Status,
    long? BuildId,
    long Values,
    long Keys,
    long LeftOut,
    long Unfilterable,
    long Labelled,
    DimensionBuildChanges Changes,
    int Requests,
    string? AggregateBy,
    string? Error,
    IReadOnlyList<string> Notes)
{
    /// <summary>How the build loaded the dimension: <c>full</c> or <c>incremental</c> (<see cref="DimensionRunModes"/>).</summary>
    public string Load { get; init; } = DimensionRunModes.Full;

    /// <summary>Where an incremental load's window began; null for a full load.</summary>
    public DateTime? WindowFrom { get; init; }

    /// <summary>Up to when the build read what changed, which the next incremental load reads on from.</summary>
    public DateTime? WindowTo { get; init; }

    /// <summary>The records an incremental load found changed in its window; null for a full load.</summary>
    public long? ChangedRecords { get; init; }

    /// <summary>The keys an incremental load read again; null for a full load.</summary>
    public long? TouchedKeys { get; init; }
}

/// <summary>
/// How a run asks its build to load: as the flow declares (in full, or incrementally for a flow with an <c>incremental</c>
/// block), in full whatever the flow declares (SQLFlow's <c>fullLoad</c>), or over the window it names (SQLFlow's backfill
/// window, <c>[From, To)</c>, either end open), which only a flow loading incrementally takes.
/// </summary>
public sealed record DimensionLoadRequest(bool FullLoad = false, DateTime? From = null, DateTime? To = null)
{
    /// <summary>Load as the flow declares.</summary>
    public static DimensionLoadRequest AsDeclared { get; } = new();

    /// <summary>Whether the run names the window its incremental load reads.</summary>
    public bool HasWindow => From is not null || To is not null;
}

/// <summary>
/// One view as a build run left it (osdu/docs/reference/flow/dimension-build.md, What a run returns): written, unchanged or
/// failed, why it failed, and what its check found in the run's partition: the rows, whether it could be read, and what
/// it has to say.
/// </summary>
public sealed record DimensionViewSummary(string View, string ViewName, string Status, string? Error, string? Check, long? Rows, IReadOnlyList<string> Notes)
{
    /// <summary>Whether the view stopped the run: it could not be written, or its check could not read it.</summary>
    public bool Failed => Status == DimensionViewWriteStatus.Failed || Check == DimensionViewCheckStatus.Failed;

    /// <summary>A view's outcome as the run's result carries it.</summary>
    public static DimensionViewSummary Of(DimensionViewOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new DimensionViewSummary(
            outcome.Name, outcome.ViewName, outcome.Status, outcome.Error ?? outcome.Check?.Error, outcome.Check?.Status, outcome.Check?.Rows,
            (outcome.Check?.Notes() ?? []).Take(DimensionBuildOutcome.MaxNotes).ToList());
    }
}

/// <summary>
/// The <c>result</c> of a build run of a dimension flow: every dimension the run selected, built, failed or skipped, and
/// every view of the flow, written and checked, with those the flow no longer declares dropped. The whole of every build
/// is in the ledger; the run's own result stays small enough for the run list to show it.
/// </summary>
public sealed record DimensionBuildOutcome(string Operation, string Flow, string Partition, int Built, int Failed, int Skipped, IReadOnlyList<DimensionBuildSummary> Dimensions)
{
    /// <summary>The most notes a dimension's summary carries in the run's result; every note is on its build in the ledger.</summary>
    public const int MaxNotes = 5;

    /// <summary>The flow's views as the run wrote and checked them, in the order the document declares them.</summary>
    public IReadOnlyList<DimensionViewSummary> Views { get; init; } = [];

    /// <summary>The views the flow made before and no longer declares, which the run dropped.</summary>
    public IReadOnlyList<string> ViewsDropped { get; init; } = [];

    /// <summary>The views that stopped the run.</summary>
    public int ViewsFailed => Views.Count(v => v.Failed);

    /// <summary>The keys the run's builds hold, which the run list shows beside the run as the work it did.</summary>
    public long RowsLoaded => Dimensions.Sum(d => d.Keys);

    /// <summary>What the run came to, as a run's error states it when a build or a view failed it.</summary>
    public string Describe()
    {
        var failed = Dimensions.Where(d => d.Status == DimensionRunStatus.Failed).Select(d => $"{d.Dimension} ({d.Error})").Take(5).ToList();
        var views = Views.Where(v => v.Failed).Select(v => $"{v.View} ({v.Error})").Take(5).ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"dimension flow '{Flow}' in partition '{Partition}': {Built} dimension(s) built, {Failed} failed{(failed.Count > 0 ? ": " + string.Join("; ", failed) : string.Empty)}{(Skipped > 0 ? $", {Skipped} not built in this partition" : string.Empty)}{(Views.Count > 0 ? $"; {Views.Count - ViewsFailed} view(s) written and checked, {ViewsFailed} failed{(views.Count > 0 ? ": " + string.Join("; ", views) : string.Empty)}" : string.Empty)}.");
    }
}

/// <summary>A build run in which a dimension failed: it carries the outcome, so the run still reports every dimension.</summary>
public sealed class DimensionBuildsFailedException : DeliveryException
{
    public DimensionBuildsFailedException(DimensionBuildOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public DimensionBuildOutcome Outcome { get; }
}

/// <summary>What a plan found of one dimension: the field it would read, the kinds and records it would read them from, and what stops it.</summary>
public sealed record DimensionPlanSummary(
    string Dimension, string Kind, string? Query, string Path, string? AggregateBy, bool? Repeats, long? Records, IReadOnlyList<DimensionKind> Kinds,
    IReadOnlyList<string> Problems, string? Skipped)
{
    /// <summary>How a build started now would load it: <c>full</c> or <c>incremental</c>; null when that cannot be told without the module's database.</summary>
    public string? Load { get; init; }

    /// <summary>Why a flow loading incrementally would load it in full; null otherwise.</summary>
    public string? FullBecause { get; init; }

    /// <summary>The window an incremental load would read, <c>[WindowFrom, WindowTo)</c>; null for a full load.</summary>
    public DateTime? WindowFrom { get; init; }

    /// <summary>Where that window would end.</summary>
    public DateTime? WindowTo { get; init; }

    /// <summary>The records that changed in that window, by the time each last changed; null for a full load.</summary>
    public long? ChangedRecords { get; init; }
}

/// <summary>
/// What a plan found of one view: the tables it reads, the statement a build writes it with, what stops a build writing it,
/// and what a build will do with it.
/// </summary>
public sealed record DimensionViewPlanSummary(string View, string ViewName, IReadOnlyList<string> Tables, string Sql, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

/// <summary>The <c>result</c> of a plan run of a dimension flow: every dimension selected, checked and counted, and every view laid out, nothing read or kept.</summary>
public sealed record DimensionPlanOutcome(string Operation, string Flow, string Partition, IReadOnlyList<DimensionPlanSummary> Dimensions)
{
    /// <summary>The flow's views as a build would write them.</summary>
    public IReadOnlyList<DimensionViewPlanSummary> Views { get; init; } = [];

    /// <summary>The dimensions the plan checked, which the run list shows beside the run.</summary>
    public long RowsLoaded => Dimensions.Count;
}

/// <summary>
/// Builds a dimension flow's dimensions in one partition (osdu/docs/reference/flow/dimension-build.md, What a build does):
/// for each dimension, settles how the index stores its field (from the template the flow reads of every kind its
/// pattern matches, or the indexer's mapping of the record's own properties), reads every distinct value through the search's
/// aggregation paged by value ranges, cleans the values into members, gives each member its search filter and, where
/// asked, its exact record count, and writes the build in one transaction. Dimensions build as many at once as the
/// flow's concurrency allows, and one that fails leaves the others building. The plan settles the fields and counts the
/// records, reading no value and keeping nothing.
/// </summary>
public sealed partial class DimensionRunner
{
    /// <summary>The most kinds one dimension's pattern is read over.</summary>
    private const int MaxKinds = 10_000;

    /// <summary>How many values a build lays out, or counts the records of, before it stages them.</summary>
    private const int MembersPerBatch = 1_000;

    private static readonly JsonSerializerOptions StepJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EngineContext _context;
    private readonly DimensionFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ILogger _log;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    /// <param name="context">The run's engine context.</param>
    /// <param name="flow">The flow bound to the partition the run builds in (or a flow whose partition is its header's).</param>
    /// <param name="values">The flow's parameter values, resolved.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="transport">Replaces the built transport (null builds the configured one); it exists for tests.</param>
    /// <param name="allowLoopback">Lets the URL guard accept a loopback endpoint; it exists for tests.</param>
    public DimensionRunner(
        EngineContext context, DimensionFlowDefinition flow, IReadOnlyDictionary<string, string> values, ILogger log,
        HttpMessageHandler? transport = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _flow = flow;
        _values = values;
        _log = log;
        _transport = transport;
        _allowLoopback = allowLoopback || EngineContext.LoopbackAllowed;
    }

    private DateTime Now => _context.Time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The most requests a build asks of the search at once, its dimensions together: <c>recordsAtOnce</c> dimensions build at
    /// once, each asking up to <c>recordsAtOnce</c> at once, so the build as a whole asks up to their product, and a dimension's
    /// labels and attributes may take the places the others leave free. The last dimension building asks as much as the
    /// whole build may, where it asked as much as its own share.
    /// </summary>
    private int RequestsAtOnce
    {
        get
        {
            var each = Math.Max(1, _flow.Parallelism.RecordsAtOnce);
            return (int)Math.Min((long)each * each, int.MaxValue);
        }
    }

    /// <summary>Builds the dimensions the run selects (every one when it names none), each loaded as the flow declares, and keeps each build.</summary>
    public Task<DimensionBuildOutcome> BuildAsync(IReadOnlyCollection<string> names, Guid runId, string actor, CancellationToken ct)
        => BuildAsync(names, DimensionLoadRequest.AsDeclared, runId, actor, ct);

    /// <summary>
    /// Builds the dimensions the run selects (every one when it names none) and keeps each build: each loaded as the flow
    /// declares (in full, or incrementally for a flow with an <c>incremental</c> block) unless <paramref name="load"/> asks
    /// for a full load or names the window an incremental load reads.
    /// </summary>
    public async Task<DimensionBuildOutcome> BuildAsync(IReadOnlyCollection<string> names, DimensionLoadRequest load, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (load.HasWindow && _flow.Incremental is null)
        {
            throw new DeliveryException(
                $"Dimension flow '{_flow.Name}' declares no incremental block, so every build loads its dimensions in full and a backfill window has nothing to bound. Declare incremental: in the flow to load what changed in a window, or run without the window.");
        }

        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        await ledger.RegisterLedgerAsync(
            new LedgerEntry
            {
                FlowId = _flow.LedgerId,
                Partition = partition,
                Kind = LedgerKinds.Dimension,
                FlowName = _flow.Name,
                LedgerName = _flow.LedgerName,
            },
            ct).ConfigureAwait(false);

        // A flow declaring views names the module's database as the pipelines reading them do; a build that would declare
        // tables it does not write stops before it builds anything.
        if (_flow.Views.Count > 0 && await TargetProblemAsync(ledger, ct).ConfigureAwait(false) is { } refused)
        {
            throw new DeliveryException(refused);
        }

        var skipped = selected.Where(d => !d.BuildsIn(partition)).ToList();
        var building = selected.Where(d => d.BuildsIn(partition)).ToList();
        _log.LogInformation(
            "dimension flow '{Flow}' in partition '{Partition}': {Count} dimension(s) to build{Skipped}",
            _flow.Name, partition, building.Count,
            skipped.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {skipped.Count} not built in this partition") : string.Empty);

        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging, RequestsAtOnce);
        using var templates = new TemplateCache(_context.DocumentTemplates, _flow.SourcePath);
        var summaries = new List<DimensionBuildSummary>(selected.Count);
        var gate = new Lock();
        await Parallel.ForEachAsync(
            building,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Parallelism.RecordsAtOnce), CancellationToken = ct },
            async (dimension, token) =>
            {
                var summary = await BuildOneAsync(ledger, search, templates, dimension, partition, load, runId, actor, token).ConfigureAwait(false);
                lock (gate)
                {
                    summaries.Add(summary);
                }
            }).ConfigureAwait(false);

        foreach (var dimension in skipped)
        {
            summaries.Add(new DimensionBuildSummary(
                dimension.Name, "skipped", null, 0, 0, 0, 0, 0, DimensionBuildChanges.None, 0, null,
                $"The dimension is built in {PartitionNames.Listed(dimension.Partitions)}, not in '{partition}'.", []));
        }

        var order = selected.Select((d, i) => (d.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        var ordered = summaries.OrderBy(s => order[s.Dimension]).ToList();

        // The flow's views are written after its dimensions, whichever the run built and even when one failed (its table
        // holds its last build), and those it no longer declares are dropped.
        var views = await WriteViewsAsync(ledger, partition, runId, actor, ct).ConfigureAwait(false);
        var outcome = new DimensionBuildOutcome(
            DeliveryOperations.Build, _flow.Name, partition,
            ordered.Count(s => s.Status == DimensionRunStatus.Completed), ordered.Count(s => s.Status == DimensionRunStatus.Failed), skipped.Count, ordered)
        {
            Views = views.Views.Select(DimensionViewSummary.Of).ToList(),
            ViewsDropped = views.Dropped,
        };
        return outcome.Failed > 0 || outcome.ViewsFailed > 0 ? throw new DimensionBuildsFailedException(outcome) : outcome;
    }

    /// <summary>
    /// Writes and checks the flow's views in the run's partition (osdu/docs/reference/flow/dimension-views.md) and
    /// drops those it no longer declares, logging each.
    /// </summary>
    private async Task<DimensionViewsWritten> WriteViewsAsync(ILedger ledger, string partition, Guid runId, string actor, CancellationToken ct)
    {
        var written = await ledger.WriteDimensionViewsAsync(
            new DimensionViewWrite
            {
                Flow = _flow.Name,
                FlowLedgerId = _flow.LedgerId,
                Partition = partition,
                Views = _flow.Views.Select(v => new DimensionViewToWrite(v, DimensionViews.TablesOf(_flow, v))).ToList(),
                RunId = runId,
                Actor = actor,
                Now = Now,
            },
            ct).ConfigureAwait(false);
        foreach (var view in written.Views)
        {
            if (view.Failed)
            {
                _log.LogError("view {View}: {Status}{Check}: {Error}", view.Name, view.Status, view.Check is null ? string.Empty : ", check " + view.Check.Status, view.Error ?? view.Check?.Error);
                continue;
            }

            _log.LogInformation(
                "view {View} ({ViewName}): {Status}, {Rows} row(s) in partition '{Partition}'{Kept}{Notes}", view.Name, DimensionTables.Shown(view.ViewName), view.Status,
                view.Check?.Rows ?? 0, partition,
                view.Check is { ReusedFrom: not null } kept
                    ? string.Create(CultureInfo.InvariantCulture, $", checked as before: nothing it reads changed since its check of {OsduSearch.LuceneTime(kept.ReusedCheckedUtc ?? kept.CheckedUtc)}, so it was not read again")
                    : string.Empty,
                view.Check?.Notes() is { Count: > 0 } notes ? "; " + string.Join(" ", notes) : string.Empty);
        }

        foreach (var name in written.Dropped)
        {
            _log.LogInformation("view {View}: dropped, the flow no longer declares it", name);
        }

        return written;
    }

    /// <summary>
    /// Why the flow's <c>target.connection</c> cannot stand for the module's database, or null when it reaches it: the
    /// server is asked on both connections for its name, the database's and when the database was made, so two spellings
    /// of one server agree. The message names the reference, never what it resolves to.
    /// </summary>
    private async Task<string?> TargetProblemAsync(ILedger ledger, CancellationToken ct)
    {
        if (_flow.Target is not { } target)
        {
            return $"Dimension flow '{_flow.Name}' declares views and no target.connection, the module's database as the pipelines reading the views name it (${{env:{OsduModuleDatabase.CatalogConnectionVariable}}}).";
        }

        var named = target.Connection.StartsWith("${", StringComparison.Ordinal) ? $"target.connection {target.Connection}" : "target.connection";
        DatabaseIdentity module;
        DatabaseIdentity declared;
        try
        {
            module = await ledger.DatabaseIdentityAsync(ct).ConfigureAwait(false);
            await using var connection = await IngestionConnection.OpenAsync(target.Connection, _flow.Name, _context.Secrets, ct).ConfigureAwait(false);
            declared = await SqlServerDimensionViewStore.IdentityAsync(connection, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or SqlFlowException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            return $"Dimension flow '{_flow.Name}': {named} could not be opened to check that it reaches the module's database, so no view was written: {SecretHygiene.RedactedMessage(ex.InnerException ?? ex)}";
        }

        return string.Equals(module.Server, declared.Server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(module.Database, declared.Database, StringComparison.OrdinalIgnoreCase)
            && module.CreatedUtc == declared.CreatedUtc
                ? null
                : $"Dimension flow '{_flow.Name}': {named} reaches another database than this host's module database, so the lineage it declares would name tables and views the build does not write; nothing was built. Point it at the module's database, the catalog's: ${{env:{OsduModuleDatabase.CatalogConnectionVariable}}}.";
    }

    /// <summary>Settles the field of every dimension the run selects and counts the records each would read, reading no value.</summary>
    public Task<DimensionPlanOutcome> PlanAsync(IReadOnlyCollection<string> names, CancellationToken ct)
        => PlanAsync(names, DimensionLoadRequest.AsDeclared, ct);

    /// <summary>
    /// Settles the field of every dimension the run selects and counts the records each would read, reading no value; says
    /// how a build asked as <paramref name="load"/> asks would load each, and for an incremental load the records that
    /// changed in its window.
    /// </summary>
    public async Task<DimensionPlanOutcome> PlanAsync(IReadOnlyCollection<string> names, DimensionLoadRequest load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(load);
        if (load.HasWindow && _flow.Incremental is null)
        {
            throw new DeliveryException(
                $"Dimension flow '{_flow.Name}' declares no incremental block, so every build loads its dimensions in full and a backfill window has nothing to bound. Declare incremental: in the flow to load what changed in a window, or plan without the window.");
        }

        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log, _flow.Source.Paging);
        using var templates = new TemplateCache(_context.DocumentTemplates, _flow.SourcePath);
        var plans = new List<DimensionPlanSummary>(selected.Count);
        foreach (var dimension in selected)
        {
            ct.ThrowIfCancellationRequested();
            var query = Query(dimension, partition);
            if (!dimension.BuildsIn(partition))
            {
                plans.Add(new DimensionPlanSummary(dimension.Name, dimension.Kind, query, dimension.Path, null, null, null, [], [],
                    $"built in {PartitionNames.Listed(dimension.Partitions)}, not in '{partition}'"));
                continue;
            }

            var problems = new List<string>();
            long? records = null;
            ResolvedField? resolved = null;
            LoadPlan? loading = null;
            long? changedRecords = null;
            try
            {
                if (_context.Ledger is { } ledger)
                {
                    loading = await PlanLoadAsync(ledger, dimension, query, load, Now, ct).ConfigureAwait(false);
                    if (loading.Mode == DimensionRunModes.Incremental)
                    {
                        changedRecords = await search.CountAsync(
                            new OsduSearchQuery
                            {
                                Kind = dimension.Kind,
                                Query = OsduSearch.Within(query, RecordChanges.Within(RecordChanges.ModifyTime, loading.From, loading.To)),
                                ReturnedFields = ["id"],
                            },
                            ct).ConfigureAwait(false);
                    }
                }

                records = await search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = query, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
                resolved = await ResolveFieldAsync(search, templates, dimension, query, ct).ConfigureAwait(false);
                if (resolved.Field is null)
                {
                    problems.Add(resolved.Note ?? "The field could not be settled.");
                }
                else
                {
                    foreach (var attribute in dimension.Attributes.Where(a => a.IsCollected))
                    {
                        await ResolvePathAsync(search, templates, dimension, attribute.Collect!, CollectedWho(dimension, attribute), resolved.Kinds, query, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (Expected(ex, ct))
            {
                problems.Add(SecretHygiene.RedactedMessage(ex));
            }

            _log.LogInformation(
                "plan {Dimension} ({Kind} {Path}): {Records}{Field}{Load}{Problems}",
                dimension.Name, dimension.Kind, dimension.Path, records is { } n ? string.Create(CultureInfo.InvariantCulture, $"{n} record(s)") : "not counted",
                resolved?.Field is { } f ? $", read as {f.AggregateBy}" : string.Empty,
                loading switch
                {
                    { Mode: DimensionRunModes.Incremental } => string.Create(CultureInfo.InvariantCulture,
                        $", loaded incrementally: {changedRecords?.ToString(CultureInfo.InvariantCulture) ?? "some"} record(s) changed in [{OsduSearch.LuceneTime(loading.From)}, {OsduSearch.LuceneTime(loading.To)})"),
                    { FullBecause: { } why } => $", loaded in full, since {why}",
                    _ => string.Empty,
                },
                problems.Count > 0 ? "; " + problems[0] : string.Empty);
            plans.Add(new DimensionPlanSummary(
                dimension.Name, dimension.Kind, query, dimension.Path, resolved?.Field?.AggregateBy, resolved?.Field is null ? null : resolved.Repeats, records,
                resolved?.Kinds ?? [], problems, null)
            {
                Load = loading?.Mode,
                FullBecause = loading?.FullBecause,
                WindowFrom = loading?.Mode == DimensionRunModes.Incremental ? loading.From : null,
                WindowTo = loading?.Mode == DimensionRunModes.Incremental ? loading.To : null,
                ChangedRecords = changedRecords,
            });
        }

        return new DimensionPlanOutcome(DeliveryOperations.Plan, _flow.Name, partition, plans) { Views = await PlanViewsAsync(ct).ConfigureAwait(false) };
    }

    /// <summary>
    /// The flow's views as a build would write them, writing nothing: the statement of each, what stops a build writing it
    /// (a <c>target.connection</c> that does not reach the module's database, a name another flow's view or an object no
    /// build made holds), and what a build will do.
    /// </summary>
    private async Task<IReadOnlyList<DimensionViewPlanSummary>> PlanViewsAsync(CancellationToken ct)
    {
        if (_flow.Views.Count == 0)
        {
            return [];
        }

        var ledger = _context.Ledger;
        string? target;
        IReadOnlyList<DimensionViewProbe> probes;
        if (ledger is null)
        {
            target = DeliveryServices.NoLedgerMessage;
            probes = [];
        }
        else
        {
            target = await TargetProblemAsync(ledger, ct).ConfigureAwait(false);
            probes = await ledger.ProbeDimensionViewsAsync(_flow.Name, _flow.Views, ct).ConfigureAwait(false);
        }

        return _flow.Views.Select(view =>
        {
            var probe = probes.FirstOrDefault(p => string.Equals(p.Name, view.Name, StringComparison.OrdinalIgnoreCase));
            var problems = new List<string>();
            if (target is not null)
            {
                problems.Add(target);
            }

            problems.AddRange(probe?.Problems ?? []);
            return new DimensionViewPlanSummary(view.Name, view.ViewName, view.Definition.Tables, view.Definition.CreateSql, problems, probe?.Notes ?? []);
        }).ToList();
    }

    private async Task<DimensionBuildSummary> BuildOneAsync(
        ILedger ledger, OsduSearch search, TemplateCache templates, DimensionSpec dimension, string partition, DimensionLoadRequest request, Guid runId,
        string actor, CancellationToken ct)
    {
        var query = Query(dimension, partition);
        var started = Now;
        var load = await PlanLoadAsync(ledger, dimension, query, request, started, ct).ConfigureAwait(false);
        var (_, run) = await ledger.StartDimensionRunAsync(
            new DimensionDeclaration
            {
                FlowId = _flow.LedgerId,
                FlowName = _flow.Name,
                Name = dimension.Name,
                Description = dimension.Description,
                Kind = dimension.Kind,
                Query = query,
                Path = dimension.Path,
                CleanJson = JsonSerializer.Serialize(dimension.Clean, StepJson),
                LabelJson = dimension.Label.Count == 0 ? null : JsonSerializer.Serialize(dimension.Label),
                AttributesJson = AttributesText(dimension.Attributes),
                ElementsJson = ElementsText(dimension.Elements),
                DefinitionHash = dimension.DefinitionHash,
            },
            runId, actor, started, load.Mode, ct).ConfigureAwait(false);
        var progress = new ReadProgress(load.Stamp(DimensionReadCounts.None));
        try
        {
            var resolved = await ResolveFieldAsync(search, templates, dimension, query, ct).ConfigureAwait(false);
            if (load.Mode == DimensionRunModes.Incremental)
            {
                var incremental = await LoadIncrementallyAsync(ledger, search, templates, dimension, query, resolved, load, run, progress, ct).ConfigureAwait(false);
                if (incremental.Summary is { } loaded)
                {
                    return loaded;
                }

                // What the incremental load found makes a full load the cheaper or the only true one: the same build loads in full.
                load = load.InFull(incremental.FullBecause!);
                progress.Read = load.Stamp(DimensionReadCounts.None);
                _log.LogInformation("dimension {Dimension}: loading in full, since {Why}", dimension.Name, incremental.FullBecause);
            }
            else if (load.FullBecause is { } why)
            {
                _log.LogInformation("dimension {Dimension}: loading in full, since {Why}", dimension.Name, why);
            }

            return await LoadInFullAsync(ledger, search, templates, dimension, query, resolved, load, run, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseAsync(ledger, run.DimensionRunId, DimensionRunStatus.Cancelled, progress.Read, "the run was cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            var error = SecretHygiene.RedactedMessage(ex);
            _log.LogError(RunFailure.IsExpected(ex) ? null : ex, "dimension {Dimension} failed: {Error}", dimension.Name, error);
            await CloseAsync(ledger, run.DimensionRunId, DimensionRunStatus.Failed, progress.Read, error).ConfigureAwait(false);
            var read = progress.Read;
            return new DimensionBuildSummary(dimension.Name, DimensionRunStatus.Failed, run.DimensionRunId, 0, 0, 0, 0, 0, DimensionBuildChanges.None,
                read.Aggregations + read.ScanPages, null, error, read.Notes.Take(DimensionBuildOutcome.MaxNotes).ToList())
            {
                Load = read.Mode,
                WindowFrom = read.WindowFrom,
                WindowTo = read.WindowTo,
                ChangedRecords = read.ChangedRecords,
                TouchedKeys = read.TouchedKeys,
            };
        }
    }

    /// <summary>
    /// Loads a dimension in full: every key the index holds read and staged, then labelled and cleaned a batch at a time, its
    /// collected values and elements read, its values laid out, all of it staged, and the dimension written whole from the
    /// staging, so what the read did not find is removed.
    /// </summary>
    private async Task<DimensionBuildSummary> LoadInFullAsync(
        ILedger ledger, OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, ResolvedField resolved, LoadPlan load,
        DimensionRunState run, ReadProgress progress, CancellationToken ct)
    {
        var templatesJson = resolved.Kinds.Count == 0 ? null : JsonSerializer.Serialize(resolved.Kinds, StepJson);

        // A flow that loads incrementally keeps, by each record's unique key, the keys it holds, so its incremental loads read
        // again what a changed record held before; one that loads in full keeps none.
        var incremental = _flow.Incremental;
        if (resolved.Field is not { } field)
        {
            // No record of the kind: nothing to settle the field by, and nothing to read. The dimension holds no value.
            progress.Read = load.Stamp(new DimensionReadCounts { Records = 0, Templates = templatesJson, Notes = [resolved.Note ?? "No record matched."] }) with
            {
                RecordKey = incremental?.RecordKey,
            };
            var empty = await ledger.WriteDimensionAsync(Nothing(run, dimension, progress.Read, keepsRecords: incremental is not null), ct).ConfigureAwait(false);
            _log.LogWarning("dimension {Dimension}: {Note}", dimension.Name, resolved.Note);
            return Summary(dimension, empty, progress.Read, null);
        }

        var keyColumns = incremental is null ? null : (await SettleRecordColumnsAsync(search, templates, dimension, query, resolved, incremental, ct).ConfigureAwait(false)).Keys;
        var concurrency = Math.Max(1, _flow.Parallelism.RecordsAtOnce);
        _log.LogInformation("dimension {Dimension}: reading {Kind} {Path} as {Field}", dimension.Name, dimension.Kind, dimension.Path, field.AggregateBy);
        await using var stage = await ledger.StageDimensionAsync(_flow.LedgerId, run.DimensionId, run.DimensionRunId, ct).ConfigureAwait(false);
        try
        {
            var keys = new StagedKeys(stage);
            var values = await DistinctValues.ReadAsync(
                new SearchDistinctSource(search, dimension.Kind, query, field),
                new DistinctReadOptions(_flow.Source.AggregationSize, dimension.MaxValues, resolved.Repeats, Concurrency: concurrency),
                _log, keys, ct).ConfigureAwait(false);
            progress.Read = load.Stamp(Counts(values, templatesJson, 0, [], LabelCounts.None));

            var scope = DimensionScope.Every(query, values, field, concurrency);
            var read = await ReadKeysAsync(
                search, templates, dimension, stage, [scope], query, field, resolved, keys.TooLong, progress,
                soFar => load.Stamp(Counts(values, templatesJson, 0, [], soFar)), ct).ConfigureAwait(false);
            var notes = new List<string>(values.Notes);
            notes.AddRange(read.Notes);
            var (memberNotes, countQueries) = await MembersAsync(search, dimension, query, field, resolved.Repeats, stage, partial: false, ct).ConfigureAwait(false);
            notes.AddRange(memberNotes);
            notes.AddRange(read.Collected.Notes);
            notes.AddRange(read.ElementNotes);
            RecordsStaged? records = null;
            if (incremental is not null)
            {
                records = await new DimensionRecordReader(search, _log, concurrency).ReadAsync(dimension, [scope], field, keyColumns!, stage, ct).ConfigureAwait(false);
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{records.Read - records.Unidentified} record(s) kept by their unique key ({incremental.RecordKey}) with the {records.Held} key(s) they hold, for incremental loads{(records.Unidentified > 0 ? $"; {records.Unidentified} hold no single value at a key column and are not kept, so an incremental load does not read again what they held" : string.Empty)}."));
            }

            var counted = Counts(values, templatesJson, countQueries, notes, read.Labels, read.Collected);
            progress.Read = load.Stamp(counted with { ScanPages = counted.ScanPages + read.ElementPages + (records?.Pages ?? 0) }) with { RecordKey = incremental?.RecordKey };
            var written = await ledger.WriteDimensionAsync(
                stage, Staged(dimension, FieldState(field, resolved.Repeats), progress.Read, read.Collected, partial: false, keepsRecords: incremental is not null), ct).ConfigureAwait(false);
            _log.LogInformation(
                "dimension {Dimension}: loaded in full, {Values} value(s) from {Keys} key(s), {LeftOut} of none; {Added} arrived, {Removed} left, {Moved} moved, {Restored} came back",
                dimension.Name, written.Members, written.Originals, written.LeftOut, written.Changes.OriginalsAdded, written.Changes.OriginalsRemoved,
                written.Changes.OriginalsMoved, written.Changes.OriginalsRestored);
            return Summary(dimension, written, progress.Read, field.AggregateBy);
        }
        finally
        {
            await DiscardAsync(stage, dimension).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a build read of its keys beyond their counts, staged: the labels it read and what reading and cleaning them had to
    /// say, its collected values, and what reading its elements had to say and the pages it read.
    /// </summary>
    private sealed record KeysRead(LabelCounts Labels, IReadOnlyList<string> Notes, CollectedRead Collected, IReadOnlyList<string> ElementNotes, int ElementPages);

    /// <summary>
    /// What a build reads of the keys it staged beyond their counts, staged as it reads it: each key's label and attributes
    /// through the record it names, and the value it is cleaned into (<see cref="CleanAsync"/>); the values each collects
    /// from its own records, each scope's records found by its own query; and the objects of its nested array. Labels and
    /// collected values need nothing of each other, so a flow that may ask several things at once has them read side by
    /// side, its concurrency shared between them; one that asks a thing at a time has them read in turn. The elements are
    /// read once both are.
    /// </summary>
    private async Task<KeysRead> ReadKeysAsync(
        OsduSearch search, TemplateCache templates, DimensionSpec dimension, DimensionStage stage, IReadOnlyList<DimensionScope> scopes, string? query,
        OsduField field, ResolvedField resolved, long tooLong, ReadProgress progress, Func<LabelCounts, DimensionReadCounts> labelled, CancellationToken ct)
    {
        var throughKeys = dimension.Attributes.Where(a => !a.IsCollected).ToList();
        var reads = dimension.Label.Count > 0 || throughKeys.Count > 0;
        var collects = dimension.Attributes.Any(a => a.IsCollected);
        var concurrency = Math.Max(1, _flow.Parallelism.RecordsAtOnce);
        var together = reads && collects && concurrency > 1;
        var forLabels = together ? concurrency / 2 : concurrency;
        var forCollected = together ? concurrency - forLabels : concurrency;
        Task<(LabelCounts Labels, IReadOnlyList<string> Notes)> Cleaning()
            => CleanAsync(search, dimension, stage, field, reads, throughKeys, forLabels, tooLong, progress, labelled, ct);
        Task<CollectedRead> Collecting() => ReadCollectedAsync(search, templates, dimension, query, field, resolved, scopes, stage, forCollected, ct);
        (LabelCounts Labels, IReadOnlyList<string> Notes) cleaned;
        CollectedRead collected;
        if (together)
        {
            var cleaning = Cleaning();
            var collecting = Collecting();
            await Task.WhenAll(cleaning, collecting).ConfigureAwait(false);
            cleaned = await cleaning.ConfigureAwait(false);
            collected = await collecting.ConfigureAwait(false);
        }
        else
        {
            cleaned = await Cleaning().ConfigureAwait(false);
            collected = await Collecting().ConfigureAwait(false);
        }

        // The objects of the dimension's nested array, a row each, read in a pass of their own once the keys are known.
        IReadOnlyList<string> elementNotes = [];
        var elementPages = 0;
        if (dimension.Elements is { } elements)
        {
            var read = await new DimensionElementReader(search, _log, concurrency).ReadAsync(dimension, scopes, field, stage, ct).ConfigureAwait(false);
            elementNotes = read.Notes(elements.Path, await stage.KeysWithElementsAsync(ct).ConfigureAwait(false), scopes.Sum(s => s.Keys));
            elementPages = read.Pages;
        }

        return new KeysRead(cleaned.Labels, cleaned.Notes, collected, elementNotes, elementPages);
    }

    /// <summary>The labels a build read: the keys labelled and not, and the searches it asked.</summary>
    private sealed record LabelCounts(long Labelled, long Unlabelled, int Queries)
    {
        public static LabelCounts None { get; } = new(0, 0, 0);
    }

    /// <summary>
    /// Labels and cleans the keys the build staged, a batch at a time, in the order it staged them, and stages each as cleaned:
    /// its label and attributes read through the record it names (when the dimension reads any), the value its label or its
    /// own text cleans into, or why it belongs to none, its count and its search filter; and the records it was read through.
    /// What reading and cleaning had to say is added up over the batches, as one read of every key would have said it: the
    /// labels' notes, then the keys too long to keep (counted when they were read), those of no value by why, and those no
    /// query can carry.
    /// </summary>
    private async Task<(LabelCounts Labels, IReadOnlyList<string> Notes)> CleanAsync(
        OsduSearch search, DimensionSpec dimension, DimensionStage stage, OsduField field, bool reads, IReadOnlyList<DimensionAttributeSpec> throughKeys,
        int concurrency, long tooLong, ReadProgress progress, Func<LabelCounts, DimensionReadCounts> labelled, CancellationToken ct)
    {
        // The labels take as many places as the build's search allows, which every dimension building shares, so a dimension
        // labelled while the others are done asks for as much as the whole build may.
        var labeler = reads ? new DimensionLabeler(search, search.RequestsAtOnce > 0 ? search.RequestsAtOnce : concurrency) : null;
        var cleaner = Cleaner(dimension);
        LabelTally? tally = null;
        var counts = LabelCounts.None;
        long keys = 0;
        long attributes = 0;
        var leftOut = new Dictionary<string, int>(StringComparer.Ordinal);
        long unfilterable = 0;
        var unfilterableNamed = new List<string>();
        // A batch is as many keys as the labeler's next pass reads (DimensionLabeler.PassSizeFor), which it learns as it reads.
        int Batch() => labeler is null ? DimensionLabeler.MaxKeysPerPass : labeler.PassSizeFor(dimension.Label, throughKeys);

        // Batches are read side by side (DimensionLabeler.PassesAtOnce), so the search is asked for one while the answers of
        // another are read, and the labeler's places stay taken; each is cleaned and staged in the order of its keys, while the
        // next ones are read, so the build waits on the search and the database side by side. It holds that many batches being
        // read, and one being staged.
        var reading = new Queue<(IReadOnlyList<StagedKey> Keys, Task<KeyLabels> Labels)>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var staging = Task.CompletedTask;

        async Task TakeAsync()
        {
            var (staged, read) = reading.Dequeue();
            var labels = await read.ConfigureAwait(false);
            if (labels.Tally is { } tallied)
            {
                if (tally is null)
                {
                    tally = tallied;
                }
                else
                {
                    tally.Add(tallied);
                }
            }

            var values = new List<(long Seq, DimensionOriginalWrite Original)>(staged.Count);
            var held = new List<(string Key, DimensionAttributeState Attribute)>();
            foreach (var key in staged)
            {
                ct.ThrowIfCancellationRequested();
                var (value, attributesOf) = Cleaned(dimension, field, cleaner, key, labels);
                values.Add((key.Seq, value));
                held.AddRange(attributesOf.Select(a => (key.Key, a)));
                if (value.LeftOut is { } reason)
                {
                    leftOut[reason] = leftOut.GetValueOrDefault(reason) + 1;
                }
                else if (!value.Filterable)
                {
                    unfilterable++;
                    if (unfilterableNamed.Count < 5)
                    {
                        unfilterableNamed.Add(key.Key);
                    }
                }
            }

            await staging.ConfigureAwait(false);
            staging = StageCleanedAsync(stage, values, held, labels.Records.ToList(), ct);
            keys += staged.Count;
            attributes += labels.Attributes.Values.Sum(a => a.Count);
            counts = new LabelCounts(counts.Labelled + labels.Labelled, counts.Unlabelled + labels.Unlabelled, counts.Queries + labels.Queries);
            progress.Read = labelled(counts);
        }

        try
        {
            await foreach (var staged in stage.KeysAsync(Batch, ct).ConfigureAwait(false))
            {
                var read = labeler is null
                    ? Task.FromResult(KeyLabels.None)
                    : labeler.ReadAsync(staged.Select(k => k.Key).ToList(), dimension.Label, throughKeys, dimension.LookupKinds, stop.Token);
                reading.Enqueue((staged, read));
                if (reading.Count >= (labeler is null ? 1 : DimensionLabeler.PassesAtOnce))
                {
                    await TakeAsync().ConfigureAwait(false);
                }
            }

            while (reading.Count > 0)
            {
                await TakeAsync().ConfigureAwait(false);
            }

            await staging.ConfigureAwait(false);
        }
        catch
        {
            // The batches being read and staged end before the build fails, so nothing they do outlives the build; why the build
            // failed is what it reports.
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var (_, read) in reading)
            {
                await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await staging.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        if (reads)
        {
            _log.LogInformation(
                "labels and attributes: {Labelled} of {Keys} key(s) labelled, {Attributes} attribute(s) read through {Chains} chain(s) in {Queries} search(es)",
                counts.Labelled, keys, attributes, (dimension.Label.Count > 0 ? 1 : 0) + throughKeys.Count(a => a.Steps.Count > 0), counts.Queries);
        }

        var notes = new List<string>(tally?.Notes() ?? []);
        if (tooLong > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{tooLong} key(s) are longer than the {DimensionSpec.MaxOriginalLength} characters a dimension keeps, and are left out."));
        }

        foreach (var (reason, count) in leftOut.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{count} key(s) belong to no value: {Describe(reason)}."));
        }

        if (unfilterable > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{unfilterable} key(s) cannot be carried in a search query, so no filter finds their records: {string.Join(", ", unfilterableNamed.Select(o => $"'{Shown(o)}'"))}{(unfilterable > 5 ? ", ..." : string.Empty)}."));
        }

        return (counts, notes);
    }

    /// <summary>
    /// A key as a build stages it: its value is its label, cleaned, when it has one, and the key's own text, cleaned, when it
    /// has none (for a key naming an OSDU record, the code its id ends with, its escapes decoded), so a value is ready to show,
    /// or why it belongs to no value. The key keeps its own filter, the search that finds exactly the records holding it, and
    /// the attributes read through the record it names.
    /// </summary>
    private static (DimensionOriginalWrite Value, List<DimensionAttributeState> Attributes) Cleaned(
        DimensionSpec dimension, OsduField field, DimensionCleaner cleaner, StagedKey key, KeyLabels labels)
    {
        var found = labels.Labels.TryGetValue(key.Key, out var label) ? label : null;
        var cleaned = cleaner.Clean(ValueSourceOf(dimension, key.Key, found));
        var filterable = DimensionFilters.Filterable(field, key.Key);
        var filter = filterable ? DimensionFilters.Of(field, [key.Key])[0] : null;
        var value = cleaned.Outcome == CleanOutcome.Member
            ? new DimensionOriginalWrite(key.Key, cleaned.Value, null, cleaned.Note, key.Count, filterable, found?.Label, found?.From, filter)
            : new DimensionOriginalWrite(key.Key, null, LeftOut(cleaned.Outcome), cleaned.Note, key.Count, filterable, found?.Label, found?.From, filter);
        return (value, ReadThrough(dimension, key.Key, labels));
    }

    /// <summary>Stages a batch of keys as cleaned: their values, the attributes read through their records, and those records.</summary>
    private static async Task StageCleanedAsync(
        DimensionStage stage, IReadOnlyList<(long Seq, DimensionOriginalWrite Original)> values, IReadOnlyList<(string Key, DimensionAttributeState Attribute)> attributes,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> records, CancellationToken ct)
    {
        await stage.AddValuesAsync(values, ct).ConfigureAwait(false);
        await stage.AddAttributesAsync(attributes, ct).ConfigureAwait(false);
        await stage.AddReadThroughAsync(records, ct).ConfigureAwait(false);
    }

    /// <summary>The read counts a build has got to, so a build that fails or is cancelled closes with what it read.</summary>
    private sealed class ReadProgress(DimensionReadCounts read)
    {
        public DimensionReadCounts Read { get; set; } = read;
    }

    /// <summary>
    /// What a key's value is cleaned from: its label when one was read; for a dimension that reads a label and names the value
    /// of a key without one (<c>unlabelled</c>), that value; otherwise the key's own text as a value shows it (for a key
    /// naming an OSDU record, the code its id ends with, its escapes decoded). A build and the dimension builder's example
    /// value a key the same way.
    /// </summary>
    internal static string ValueSourceOf(DimensionSpec dimension, string original, KeyLabel? labelled)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(original);
        return labelled?.Label
            ?? (dimension.Label.Count > 0 && dimension.Unlabelled is { } unlabelled ? unlabelled : DimensionLabeler.DisplayOf(original));
    }

    /// <summary>
    /// Lays out the values the write holds from all their keys, a value at a time in the order of the values, and stages each:
    /// its count, its filter, and the exact count of its records where the dimension asks for one; says what laying out and
    /// counting had to say, a line each, and how many counts it asked. A full load lays out every value its keys are under; a
    /// partial one (an incremental load's) every value a key it read again is under now or was under before, from the keys
    /// the dimension holds besides (<see cref="DimensionStage.MemberKeysAsync"/>).
    /// </summary>
    private async Task<(List<string> Notes, int CountQueries)> MembersAsync(
        OsduSearch search, DimensionSpec dimension, string? query, OsduField field, bool repeats, DimensionStage stage, bool partial, CancellationToken ct)
    {
        // A member's count is exact where its originals' counts are counts of records that no two of them share: a field a
        // record holds once, or a list outside a nested array holding one original. Elsewhere it is their sum, unless the
        // dimension asks for exact counts, which one count of its filter gives where the filter is one query covering all.
        var notes = new List<string>();
        var splitFilters = 0;
        var laid = new List<DimensionMemberWrite>();
        string? value = null;
        long records = 0;
        var originals = 0;
        var unfilterable = 0;
        var filterable = new List<string>(DimensionFilters.MaxOriginalsPerQuery);
        var filterableCount = 0;

        async Task LayOutAsync()
        {
            var parts = (filterableCount + DimensionFilters.MaxOriginalsPerQuery - 1) / DimensionFilters.MaxOriginalsPerQuery;
            var exact = !repeats || (field.NestedPath is null && originals == 1);
            laid.Add(new DimensionMemberWrite(value!, records, exact, originals, unfilterable, parts == 1 ? DimensionFilters.Of(field, filterable)[0] : null, parts));
            if (!exact && dimension.CountRecords && !(parts == 1 && unfilterable == 0))
            {
                splitFilters++;
            }

            if (laid.Count >= MembersPerBatch)
            {
                await stage.AddMembersAsync(laid, ct).ConfigureAwait(false);
                laid.Clear();
            }
        }

        await foreach (var key in stage.MemberKeysAsync(partial, ct).ConfigureAwait(false))
        {
            if (!string.Equals(key.Value, value, StringComparison.Ordinal))
            {
                if (value is not null)
                {
                    await LayOutAsync().ConfigureAwait(false);
                }

                (value, records, originals, unfilterable, filterableCount) = (key.Value, 0, 0, 0, 0);
                filterable.Clear();
            }

            records += key.Count;
            originals++;
            if (!key.Filterable)
            {
                unfilterable++;
                continue;
            }

            filterableCount++;
            if (filterable.Count < DimensionFilters.MaxOriginalsPerQuery)
            {
                filterable.Add(key.Original);
            }
        }

        if (value is not null)
        {
            await LayOutAsync().ConfigureAwait(false);
        }

        await stage.AddMembersAsync(laid, ct).ConfigureAwait(false);
        if (splitFilters > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{splitFilters} value(s) hold keys no one query covers (more than {DimensionFilters.MaxOriginalsPerQuery}, or one no query can carry), so their records are counted as the sum of their keys'."));
        }

        if (!dimension.CountRecords)
        {
            return (notes, 0);
        }

        var countQueries = 0;
        var failedCounts = 0;
        string? firstFailure = null;
        var gate = new Lock();
        await foreach (var batch in stage.MembersToCountAsync(MembersPerBatch, ct).ConfigureAwait(false))
        {
            var counted = new List<(long Seq, long Records)>(batch.Count);
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Parallelism.RecordsAtOnce), CancellationToken = ct },
                async (member, token) =>
                {
                    try
                    {
                        var found = await search.CountAsync(
                            new OsduSearchQuery { Kind = dimension.Kind, Query = OsduSearch.Within(query, member.Filter), ReturnedFields = ["id"] }, token).ConfigureAwait(false);
                        lock (gate)
                        {
                            counted.Add((member.Seq, found));
                            countQueries++;
                        }
                    }
                    catch (DeliveryException ex)
                    {
                        lock (gate)
                        {
                            countQueries++;
                            failedCounts++;
                            firstFailure ??= SecretHygiene.RedactedMessage(ex);
                        }
                    }
                }).ConfigureAwait(false);
            await stage.CountedAsync(counted, ct).ConfigureAwait(false);
        }

        if (failedCounts > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"The records of {failedCounts} value(s) could not be counted, so they are the sum of their keys': {firstFailure}"));
        }

        return (notes, countQueries);
    }

    /// <summary>
    /// The keys the dimension's own read finds, staged as each range of them is read whole: a key longer than a dimension keeps
    /// is counted and left out, as the build's notes say.
    /// </summary>
    private sealed class StagedKeys(DimensionStage stage) : IDistinctSink
    {
        /// <summary>The keys longer than a dimension keeps, left out.</summary>
        public long TooLong { get; private set; }

        public async ValueTask AddAsync(IReadOnlyList<KeyValuePair<string, long>> values, CancellationToken ct)
        {
            var kept = values.Where(v => v.Key.Length <= DimensionSpec.MaxOriginalLength).ToList();
            TooLong += values.Count - kept.Count;
            await stage.AddKeysAsync(kept, ct).ConfigureAwait(false);
        }

        public async ValueTask ResetAsync(CancellationToken ct)
        {
            TooLong = 0;
            await stage.ResetKeysAsync(ct).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// How the index stores the dimension's field: for a property of the record itself, as the indexer maps it for every kind;
    /// for a property of data, as the template the flow reads of every kind the pattern matches in the partition says, which must all
    /// say the same. A partition holding no record of the kind settles nothing, and says so.
    /// </summary>
    private Task<ResolvedField> ResolveFieldAsync(OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, CancellationToken ct)
        => ResolvePathAsync(search, templates, dimension, dimension.Path, $"Dimension {dimension.Name}", null, query, ct);

    /// <summary>
    /// How the index stores <paramref name="path"/> of the dimension's records (<see cref="ResolveFieldAsync"/>), as
    /// <paramref name="who"/> reads it; the kinds the dimension's query matches are read again unless <paramref name="known"/>
    /// already holds them.
    /// </summary>
    private async Task<ResolvedField> ResolvePathAsync(
        OsduSearch search, TemplateCache templates, DimensionSpec dimension, string path, string who, IReadOnlyList<DimensionKind>? known, string? query,
        CancellationToken ct)
    {
        if (!SearchFields.IsDataPath(path))
        {
            var shape = SearchFields.RecordProperty(path);
            return shape.Field is { } own
                ? new ResolvedField(own, shape.Repeats, [], null)
                : throw new DeliveryException($"{who} reads {path}, which {shape.Problem}");
        }

        IReadOnlyList<(string Kind, long Records)> kinds;
        if (known is { Count: > 0 })
        {
            kinds = known.Select(k => (k.Kind, k.Records)).ToList();
        }
        else
        {
            var read = await DistinctValues.ReadAsync(
                new SearchDistinctSource(search, dimension.Kind, query, OsduField.Keyword("kind")),
                new DistinctReadOptions(_flow.Source.AggregationSize, MaxKinds, Repeats: false),
                _log, ct).ConfigureAwait(false);
            kinds = read.Values.Select(k => (k.Key, k.Value)).ToList();
        }

        if (kinds.Count == 0)
        {
            return new ResolvedField(null, false, [], $"No record of kind {dimension.Kind} matches the dimension's query in this partition, so it holds no value.");
        }

        var found = new List<DimensionKind>();
        var missing = new List<string>();
        string? why = null;
        var shapes = new List<(string Kind, IndexedShape Shape)>();
        foreach (var (kind, records) in kinds.OrderBy(k => k.Kind, StringComparer.Ordinal))
        {
            var (schema, version, problem) = await templates.NewestAsync(kind, ct).ConfigureAwait(false);
            if (schema is null)
            {
                why ??= problem;
                missing.Add(string.Create(CultureInfo.InvariantCulture, $"{kind} ({records} record(s))"));
                found.Add(new DimensionKind(kind, records, null));
                continue;
            }

            found.Add(new DimensionKind(kind, records, version));
            shapes.Add((kind, SearchFields.ClassifyValue(schema, path)));
        }

        if (missing.Count > 0)
        {
            throw new DeliveryException(
                $"{who} reads {path} of {dimension.Kind}, and the flow reads no template of {string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? $" and {missing.Count - 10} more" : string.Empty)}, so how the index stores the field there is not known: {why}");
        }

        var refused = shapes.Where(s => s.Shape.Field is null).ToList();
        if (refused.Count > 0)
        {
            throw new DeliveryException(
                $"{who} cannot read {path}: in {refused[0].Kind}, {refused[0].Shape.Problem}");
        }

        var distinct = shapes.GroupBy(s => s.Shape.Field!).ToList();
        if (distinct.Count > 1)
        {
            throw new DeliveryException(
                $"{who} reads {path}, which the kinds its pattern matches index differently: "
                + string.Join("; ", distinct.Select(g => $"{g.Key} in {string.Join(", ", g.Select(s => s.Kind).Take(5))}"))
                + ". One field is read one way; narrow the kind to the versions that agree.");
        }

        return new ResolvedField(distinct[0].Key, shapes.Any(s => s.Shape.Repeats), found, null);
    }

    /// <summary>Who reads a collected attribute's path, as a message names it.</summary>
    private static string CollectedWho(DimensionSpec dimension, DimensionAttributeSpec attribute) => $"Attribute {attribute.Name} of dimension {dimension.Name}";

    /// <summary>How the ledger keeps a field a build settled.</summary>
    private static DimensionFieldState FieldState(OsduField field, bool repeats) => new(Index(field.Index), field.NestedPath, field.AggregateBy, repeats);

    /// <summary>
    /// What a build read of its dimension's collected attributes, the values and texts staged: each attribute's field, the
    /// requests it asked, and what it has to say.
    /// </summary>
    private sealed record CollectedRead(IReadOnlyList<DimensionCollectedState> States, int Aggregations, int ScanPages, IReadOnlyList<string> Notes)
    {
        public static CollectedRead None { get; } = new([], 0, 0, []);
    }

    /// <summary>
    /// The field of each collected attribute (<see cref="DimensionAttributeSpec.Collect"/>), settled as the dimension's is, as
    /// a build keeps it: what an incremental load compares with what the last build kept before it reads anything.
    /// </summary>
    private async Task<IReadOnlyList<(DimensionAttributeSpec Attribute, OsduField Field, bool Repeats, DimensionCollectedState State)>> SettleCollectedAsync(
        OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, ResolvedField resolved, CancellationToken ct)
    {
        var settled = new List<(DimensionAttributeSpec, OsduField, bool, DimensionCollectedState)>();
        foreach (var attribute in dimension.Attributes.Where(a => a.IsCollected))
        {
            var who = CollectedWho(dimension, attribute);
            var path = await ResolvePathAsync(search, templates, dimension, attribute.Collect!, who, resolved.Kinds, query, ct).ConfigureAwait(false);
            var field = path.Field ?? throw new DeliveryException($"{who} reads {attribute.Collect}, and {path.Note ?? "its field could not be settled."}");
            settled.Add((attribute, field, path.Repeats, new DimensionCollectedState(attribute.Name, attribute.Collect!, FieldState(field, path.Repeats), dimension.Unlabelled)));
        }

        return settled;
    }

    /// <summary>
    /// The values each key collects from its own records, for each collected attribute (<see cref="DimensionAttributeSpec.Collect"/>),
    /// staged: its field settled as the dimension's is, then collected (<see cref="DimensionCollector"/>) scope by scope, each
    /// over the records its own query finds. The scopes' keys are never the same, so their rows are each key's; a text two
    /// scopes collect counts the records of both, which the write adds up.
    /// </summary>
    private async Task<CollectedRead> ReadCollectedAsync(
        OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, OsduField keyField, ResolvedField resolved,
        IReadOnlyList<DimensionScope> scopes, DimensionStage stage, int concurrency, CancellationToken ct)
    {
        if (!dimension.Attributes.Any(a => a.IsCollected))
        {
            return CollectedRead.None;
        }

        var collector = new DimensionCollector(search, _log, _flow.Source.AggregationSize, Math.Max(1, concurrency));
        var states = new List<DimensionCollectedState>();
        var notes = new List<string>();
        var aggregations = 0;
        var pages = 0;
        foreach (var (attribute, field, repeats, state) in await SettleCollectedAsync(search, templates, dimension, query, resolved, ct).ConfigureAwait(false))
        {
            foreach (var scope in scopes)
            {
                var read = await collector.CollectAsync(dimension, attribute, scope, keyField, resolved.Repeats, field, repeats, stage, ct).ConfigureAwait(false);
                notes.AddRange(read.Notes.Where(n => !notes.Contains(n, StringComparer.Ordinal)));
                aggregations += read.Aggregations;
                pages += read.ScanPages;
            }

            states.Add(state);
        }

        return new CollectedRead(states, aggregations, pages, notes);
    }

    /// <summary>
    /// Each key's attribute values: those read from the record it names, the value the dimension names for what is not read
    /// under each such attribute a key has none of, and the values it collects from its own records. The dimension builder's
    /// example gives a key its attributes this way; a build gives each key the first two as it cleans it, and stages the
    /// collected ones as it collects them.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<DimensionAttributeState>> KeyAttributes<TCollected>(
        DimensionSpec dimension, IEnumerable<string> keys, KeyLabels labels, IReadOnlyDictionary<string, TCollected> collected)
        where TCollected : IEnumerable<DimensionAttributeState>
    {
        var all = new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var values = ReadThrough(dimension, key, labels);
            if (collected.TryGetValue(key, out var collects))
            {
                values.AddRange(collects);
            }

            if (values.Count > 0)
            {
                all[key] = values;
            }
        }

        return all;
    }

    /// <summary>
    /// A key's attribute values read from the record it names, and the value the dimension names for what is not read under
    /// each such attribute the key has none of.
    /// </summary>
    private static List<DimensionAttributeState> ReadThrough(DimensionSpec dimension, string key, KeyLabels labels)
    {
        var values = new List<DimensionAttributeState>();
        if (labels.Attributes.TryGetValue(key, out var read))
        {
            values.AddRange(read);
        }

        if (dimension.Unlabelled is { } none)
        {
            values.AddRange(dimension.Attributes
                .Where(a => !a.IsCollected && values.All(v => v.Name != a.Name))
                .Select(a => new DimensionAttributeState(a.Name, none, null)));
        }

        return values;
    }

    /// <summary>The cleaner of a dimension, with every dictionary its map steps read loaded beside the flow's file.</summary>
    private DimensionCleaner Cleaner(DimensionSpec dimension)
    {
        if (dimension.Clean.Count == 0)
        {
            return DimensionCleaner.Identity;
        }

        return DimensionCleaner.Build(dimension.Clean, name =>
        {
            var folder = _flow.SourcePath is { } path
                ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
                : throw new DeliveryException(
                    $"Dimension {dimension.Name} maps through dictionary {name}, which is found beside the flow's file, and this flow was not loaded from a file.");
            try
            {
                return new DictionaryCatalog(_context.Documents).Load(name, folder, full => Path.GetRelativePath(folder, full).Replace('\\', '/')).Dictionary;
            }
            catch (FlowValidationException ex)
            {
                throw new DeliveryException($"Dimension {dimension.Name} could not read dictionary {name}: {ex.Message}", ex);
            }
        });
    }

    /// <summary>How a build's write merges what it staged: the field it settled, what it read of its collected attributes, and its table.</summary>
    private DimensionStagedWrite Staged(
        DimensionSpec dimension, DimensionFieldState field, DimensionReadCounts read, CollectedRead collected, bool partial, bool keepsRecords)
        => new()
        {
            Partial = partial,
            Field = field,
            CollectedJson = collected.States.Count == 0 ? null : JsonSerializer.Serialize(collected.States, StepJson),
            Table = DimensionTables.Of(dimension.Name, dimension.KeyColumn, dimension.ValueColumn, dimension.Attributes, dimension.Elements),
            KeepsRecords = keepsRecords,
            Read = read,
            CompletedUtc = Now,
        };

    /// <summary>The write of a build that read nothing, its field unsettled: the dimension keeps what it had, its table brought to it.</summary>
    private DimensionWrite Nothing(DimensionRunState run, DimensionSpec dimension, DimensionReadCounts read, bool keepsRecords)
        => new()
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = run.DimensionId,
            FlowId = _flow.LedgerId,
            Field = null,
            Table = DimensionTables.Of(dimension.Name, dimension.KeyColumn, dimension.ValueColumn, dimension.Attributes, dimension.Elements),
            Originals = [],
            Members = [],
            Records = keepsRecords ? new DimensionRecordsWrite([], []) : null,
            Read = read,
            CompletedUtc = Now,
        };

    /// <summary>
    /// Deletes what the build staged, written or not. A deletion that fails leaves it for the next build of the dimension,
    /// which deletes what an earlier build left; the build's own outcome stands.
    /// </summary>
    private async Task DiscardAsync(DimensionStage stage, DimensionSpec dimension)
    {
        try
        {
            await stage.DiscardAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            _log.LogWarning(
                "dimension {Dimension}: the staging of build {DimensionRunId} could not be deleted, and the next build of the dimension deletes it: {Error}",
                dimension.Name, stage.DimensionRunId, SecretHygiene.RedactedMessage(ex));
        }
    }

    private static DimensionBuildSummary Summary(DimensionSpec dimension, DimensionRunState written, DimensionReadCounts read, string? aggregateBy)
        => new(dimension.Name, written.Status, written.DimensionRunId, written.Members, written.Originals, written.LeftOut, written.Unfilterable, read.Labelled,
            DimensionBuildChanges.Of(written.Changes), read.Aggregations + read.ScanPages + read.CountQueries + read.LabelQueries, aggregateBy, null,
            read.Notes.Take(DimensionBuildOutcome.MaxNotes).ToList())
        {
            Load = read.Mode,
            WindowFrom = read.WindowFrom,
            WindowTo = read.WindowTo,
            ChangedRecords = read.ChangedRecords,
            TouchedKeys = read.TouchedKeys,
        };

    private static DimensionReadCounts Counts(DistinctCounts read, string? templates, int countQueries, IReadOnlyList<string> notes, LabelCounts labels, CollectedRead? collected = null) => new()
    {
        Labelled = labels.Labelled,
        Unlabelled = labels.Unlabelled,
        LabelQueries = labels.Queries,
        Records = read.Records,
        WithValue = read.WithValue,
        Nulls = read.Nulls,
        TooLong = read.TooLong,
        Unreadable = read.Unreadable,
        Aggregations = read.Aggregations + (collected?.Aggregations ?? 0),
        Slices = read.Slices,
        Splits = read.Splits,
        ScannedSlices = read.ScannedSlices,
        ScanPages = read.ScanPages + (collected?.ScanPages ?? 0),
        ScannedUnits = read.ScannedUnits,
        CountQueries = countQueries,
        Templates = templates,
        Notes = notes.Count > 0 ? notes : read.Notes,
    };

    private async Task CloseAsync(ILedger ledger, long dimensionRunId, string status, DimensionReadCounts read, string error)
    {
        try
        {
            await ledger.CloseDimensionRunAsync(dimensionRunId, status, read, error, Now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException || ex.GetType().Name.Contains("DbUpdate", StringComparison.Ordinal))
        {
            // The build's own outcome is what the run reports; a build left running says so on its page.
            _log.LogError("Could not close dimension build {DimensionRunId} as {Status}: {Message}", dimensionRunId, status, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>The query a dimension reads with, its parameters and the partition substituted; null for every record of the kind.</summary>
    private string? Query(DimensionSpec dimension, string partition)
        => dimension.Query is { } query
            ? FlowParameters.Substitute(query, new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition })
            : null;

    /// <summary>
    /// The partition the run builds in, by its data-partition-id: the one the flow is bound to, or for a flow whose partition is
    /// its header's, the header's resolved on this node.
    /// </summary>
    private async Task<string> PartitionAsync(CancellationToken ct)
    {
        if (_flow.Partition is { } bound)
        {
            return bound;
        }

        var declared = _flow.Source.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new DeliveryException(
                $"Dimension flow '{_flow.Name}' is bound to no partition and names no '{CacheScope.PartitionHeader}' in its source.headers, so the partition it builds in is unknown.");
        }

        return CacheScope.Normalize(await _context.Secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{_flow.Name}: source.headers");
    }

    /// <summary>What a failure of one dimension is: anything but the run itself being cancelled, which stops every dimension.</summary>
    private static bool Expected(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    private static string LeftOut(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.Empty => DimensionLeftOut.Empty,
        CleanOutcome.TooLong => DimensionLeftOut.TooLong,
        CleanOutcome.Dropped => DimensionLeftOut.Dropped,
        _ => DimensionLeftOut.Failed,
    };

    private static string Describe(string leftOut) => leftOut switch
    {
        DimensionLeftOut.Empty => "cleaning left nothing",
        DimensionLeftOut.TooLong => $"their value would be longer than the {DimensionSpec.MaxCleanLength} characters a value may be",
        DimensionLeftOut.Dropped => "a map step left them out",
        _ => "a clean step could not run on them",
    };

    /// <summary>
    /// The clean steps a build kept of its dimension (<see cref="DimensionState.CleanJson"/>), as it wrote them; none when the
    /// text is not the steps a build writes.
    /// </summary>
    public static IReadOnlyList<CleanStep> StepsOf(string? cleanJson)
    {
        if (string.IsNullOrWhiteSpace(cleanJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CleanStep>>(cleanJson, StepJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The attributes a build keeps of its dimension, as JSON (<see cref="DimensionState.AttributesJson"/>); null for none.</summary>
    internal static string? AttributesText(IReadOnlyList<DimensionAttributeSpec> attributes)
        => attributes.Count == 0
            ? null
            : JsonSerializer.Serialize(
                attributes.Select(a => new AttributeText(
                    a.Name, a.IsCollected ? null : a.Steps.ToList(), a.Collect, KeepText(a.Keep), ManyText(a.Many), DecodeText(a.Decode),
                    a.MaxReferences == DimensionAttributeSpec.DefaultMaxReferences ? null : a.MaxReferences, a.Trim ? null : false)).ToList(), StepJson);

    /// <summary>
    /// The attributes a build kept of its dimension (<see cref="DimensionState.AttributesJson"/>), in the order declared; none
    /// when it reads none or the text is not the list a build writes.
    /// </summary>
    public static IReadOnlyList<DimensionAttributeSpec> AttributesOf(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<AttributeText>>(attributesJson, StepJson) ?? [])
                .Where(a => DimensionAttributeSpec.IsName(a.Name) && (a.Steps is { Count: > 0 } || !string.IsNullOrWhiteSpace(a.Collect)))
                .Select(a => string.IsNullOrWhiteSpace(a.Collect)
                    ? new DimensionAttributeSpec(
                        a.Name, a.Steps!, null, KeepOf(a.Keep), ManyOf(a.Many), DecodeOf(a.Decode),
                        a.MaxReferences is >= 1 and <= DimensionAttributeSpec.MaxReferencesCeiling ? a.MaxReferences.Value : DimensionAttributeSpec.DefaultMaxReferences,
                        a.Trim ?? true)
                    : new DimensionAttributeSpec(a.Name, [], a.Collect, KeepOf(a.Keep)))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// An attribute as its dimension's row keeps it: the steps it is read through, or the path it collects, how its values are
    /// kept, how several of them are, and what they are decoded from.
    /// </summary>
    private sealed record AttributeText(
        string Name, List<string>? Steps, string? Collect = null, string? Keep = null, string? Many = null, string? Decode = null,
        int? MaxReferences = null, bool? Trim = null);

    /// <summary>The elements a build keeps of its dimension, as JSON (<see cref="DimensionState.ElementsJson"/>); null for none.</summary>
    internal static string? ElementsText(DimensionElementsSpec? elements)
        => elements is null
            ? null
            : JsonSerializer.Serialize(
                new ElementsJson(elements.Path, elements.Fields.Select(f => new ElementFieldText(
                    f.Name, f.Path, KeepText(f.Keep), f.Up == 0 ? null : f.Up, ManyText(f.Many), DecodeText(f.Decode), f.Trim ? null : false)).ToList(),
                    elements.KeepNulls ? true : null),
                StepJson);

    /// <summary>
    /// The elements a build kept of its dimension (<see cref="DimensionState.ElementsJson"/>); null when it reads none or the
    /// text is not what a build writes.
    /// </summary>
    public static DimensionElementsSpec? ElementsOf(string? elementsJson)
    {
        if (string.IsNullOrWhiteSpace(elementsJson))
        {
            return null;
        }

        try
        {
            var read = JsonSerializer.Deserialize<ElementsJson>(elementsJson, StepJson);
            var fields = (read?.Fields ?? [])
                .Where(f => DimensionAttributeSpec.IsName(f.Name) && !string.IsNullOrWhiteSpace(f.Path))
                .Select(f => new DimensionElementField(
                    f.Name, f.Path, KeepOf(f.Keep), Math.Max(0, f.Up ?? 0), ManyOf(f.Many), DecodeOf(f.Decode), f.Trim ?? true))
                .ToList();
            return read is null || string.IsNullOrWhiteSpace(read.Path) || fields.Count == 0 ? null : new DimensionElementsSpec(read.Path, fields, read.KeepNulls ?? false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The elements as their dimension's row keeps them: the array, and each field with its path inside the object and how it is kept.</summary>
    private sealed record ElementsJson(string Path, List<ElementFieldText>? Fields, bool? KeepNulls = null);

    private sealed record ElementFieldText(string Name, string Path, string? Keep = null, int? Up = null, string? Many = null, string? Decode = null, bool? Trim = null);

    /// <summary>How a keep is written in the JSON a build keeps: nothing for a value, so a declaration that keeps none reads as it always did.</summary>
    private static string? KeepText(DimensionValueKeep keep) => keep == DimensionValueKeep.Value ? null : DimensionKeeping.Named(keep);

    private static DimensionValueKeep KeepOf(string? keep) => DimensionKeeping.Parse(keep) ?? DimensionValueKeep.Value;

    /// <summary>How a many is written in the JSON a build keeps: nothing for the first, so a declaration that names none reads as it always did.</summary>
    private static string? ManyText(DimensionValueMany many) => many == DimensionValueMany.First ? null : DimensionKeeping.Named(many);

    private static DimensionValueMany ManyOf(string? many) => DimensionKeeping.ParseMany(many) ?? DimensionValueMany.First;

    /// <summary>How a decode is written in the JSON a build keeps: nothing for none.</summary>
    private static string? DecodeText(DimensionValueDecode decode) => decode == DimensionValueDecode.None ? null : DimensionKeeping.Named(decode);

    private static DimensionValueDecode DecodeOf(string? decode) => DimensionKeeping.ParseDecode(decode) ?? DimensionValueDecode.None;

    /// <summary>
    /// What the last build read of a dimension's collected attributes (<see cref="DimensionState.CollectedJson"/>); none when it holds
    /// none or the text is not the list a build writes.
    /// </summary>
    public static IReadOnlyList<DimensionCollectedState> CollectedOf(string? collectedJson)
    {
        if (string.IsNullOrWhiteSpace(collectedJson))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<DimensionCollectedState>>(collectedJson, StepJson) ?? [])
                .Where(h => DimensionAttributeSpec.IsName(h.Name) && h.Field is not null && !string.IsNullOrWhiteSpace(h.Path))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The label paths a build kept of its dimension (<see cref="DimensionState.LabelJson"/>), as it wrote them; none when the
    /// dimension reads no label or the text is not the list a build writes.
    /// </summary>
    public static IReadOnlyList<string> LabelOf(string? labelJson)
    {
        if (string.IsNullOrWhiteSpace(labelJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(labelJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The kinds a build read, as it kept them with its counts (<see cref="DimensionReadCounts.Templates"/>); none when there are none.</summary>
    public static IReadOnlyList<DimensionKind> KindsOf(string? templatesJson)
    {
        if (string.IsNullOrWhiteSpace(templatesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DimensionKind>>(templatesJson, StepJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>How the ledger names the way a field is stored.</summary>
    public static string Index(OsduFieldIndex index) => index switch
    {
        OsduFieldIndex.Text => "text",
        OsduFieldIndex.Keyword => "keyword",
        OsduFieldIndex.Number => "number",
        OsduFieldIndex.Boolean => "boolean",
        _ => "date",
    };

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;

    private sealed record ResolvedField(OsduField? Field, bool Repeats, IReadOnlyList<DimensionKind> Kinds, string? Note);

    /// <summary>
    /// The template of each exact kind a dimension reads, as the flow reads it: the one version of the kind the template
    /// folders on the flow's path hold (osdu/docs/reference/concepts/templates.md, Template folders), read once per run
    /// whichever dimension asks.
    /// </summary>
    private sealed class TemplateCache(ITemplateSource source, string? document) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, (SchemaSnapshot? Schema, string? Version, string? Problem)> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose() => _gate.Dispose();

        /// <summary>The schema of the template of <paramref name="kind"/> the flow reads, with its version; nulls and why when it reads none.</summary>
        public async Task<(SchemaSnapshot? Schema, string? Version, string? Problem)> NewestAsync(string kind, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_loaded.TryGetValue(kind, out var known))
                {
                    return known;
                }

                var resolved = await source.ResolveAsync(document, kind, null, ct).ConfigureAwait(false);
                var loaded = resolved.Found
                    ? (resolved.Schema, resolved.Template!.Version, (string?)null)
                    : ((SchemaSnapshot?)null, (string?)null, resolved.Problem);
                _loaded[kind] = loaded;
                return loaded;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
