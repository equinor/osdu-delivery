using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>dimensions</c> verb: a dimension flow's dimensions where an operator already is, at a terminal or in a script,
/// without a control plane to reach (osdu/docs/reference/cli/dimensions.md). <c>list</c> shows each dimension and its last
/// build, <c>values</c> and <c>keys</c> page through what a dimension holds (the human-friendly values, and the keys exactly
/// as the index holds them), <c>filter</c> writes the search that finds the records of the values named, <c>search</c>
/// composes the search across the flow's dimensions from the values picked in each, <c>history</c> lists the builds and
/// <c>changes</c> the change log, and <c>export</c> writes the whole as CSV or JSON Lines; the filter, the search and the
/// export are written by the same code as the API's; <c>attributes</c> lists the values an attribute of a dimension's keys
/// holds (among the keys the other picks leave, for a cascade of selects), <c>--attr</c> narrows values and keys by them, and
/// <c>export --set table</c> writes the table a cascade of selects is read from; <c>remove</c> removes a dimension the flow
/// no longer declares, and everything kept of it, for good; <c>views</c> lists the flow's views
/// (osdu/docs/reference/flow/dimension-views.md), or shows one with its columns, checks and SQL, and <c>remove-view</c>
/// removes a view the flow no longer declares. Building is a run like any other: <c>sqlflow run &lt;flow.yaml&gt; --payload
/// '{"dimensions":["name"]}'</c>.
/// </summary>
internal static class DeliveryDimensionVerbs
{
    /// <summary>Values, keys, builds or changes listed when the command line asks for no count.</summary>
    private const int DefaultMax = 50;

    /// <summary>The keys a value's line names beside it.</summary>
    private const int ShownKeys = 3;

    public static async Task<int> DimensionsAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the dimension flow document whose dimensions these are.");
        }

        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Dimensions live in the module's database. Run 'sqlflow dimensions' with --db <conn-ref>, or set the catalog variable.");
        var flow = await CliPartitions.DimensionAsync(context, engine.Documents.LoadDimension(flowPath), ct).ConfigureAwait(false);
        if (verb == "list")
        {
            return await ListAsync(context, ledger, flow, ct).ConfigureAwait(false);
        }

        if (verb == "search")
        {
            return await SearchAsync(context, ledger, flow, ct).ConfigureAwait(false);
        }

        if (verb == "remove")
        {
            return await RemoveAsync(context, ledger, flow, engine.Time, ct).ConfigureAwait(false);
        }

        if (verb == "views")
        {
            return context.Arguments.GetOption("--suggest") is { } from && !string.IsNullOrWhiteSpace(from)
                ? await SuggestAsync(context, flow, from.Trim(), engine.DocumentTemplates, ct).ConfigureAwait(false)
                : await ViewsAsync(context, ledger, flow, ct).ConfigureAwait(false);
        }

        if (verb == "remove-view")
        {
            return await RemoveViewAsync(context, ledger, flow, engine.Time, ct).ConfigureAwait(false);
        }

        if (verb is not ("table" or "values" or "keys" or "attributes" or "filter" or "history" or "changes" or "export"))
        {
            return context.UsageError("say what to do: list, table, values, keys, attributes, filter, search, history, changes, export, remove, views or remove-view.");
        }

        var dimension = await DimensionAsync(context, ledger, flow, ct).ConfigureAwait(false);
        return verb switch
        {
            "table" => await TableAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "values" => await ValuesAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "keys" => await KeysAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "attributes" => await AttributesAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "filter" => await FilterAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "history" => await HistoryAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            "changes" => await ChangesAsync(context, ledger, dimension, ct).ConfigureAwait(false),
            _ => await ExportAsync(context, ledger, dimension, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Removes the dimension --dimension names, which the flow file no longer declares, and everything kept of it in the
    /// flow's partition, for good (<see cref="DimensionRemoval"/>): one the file declares is refused, and so is one a cache
    /// flow captures. The removal is an activity of the flow, recorded under the operating system user and machine that ran
    /// it (<c>cli:&lt;user&gt;@&lt;machine&gt;</c>), as every command line action is.
    /// </summary>
    private static async Task<int> RemoveAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, TimeProvider clock, CancellationToken ct)
    {
        if (context.Arguments.GetOption("--dimension") is not { } asked || string.IsNullOrWhiteSpace(asked))
        {
            return context.UsageError("name the dimension to remove with --dimension <name>: one the flow no longer declares.");
        }

        var name = asked.Trim();
        if (flow.Dimension(name) is { } declared)
        {
            throw new FlowValidationException(
                $"{flow.Name} declares dimension {declared.Name}, so it is not removed. Take it out of the flow's file first: only a dimension the flow no longer declares is removed.");
        }

        var held = await ledger.ListDimensionsAsync(null, flow.LedgerId, ct).ConfigureAwait(false);
        var dimension = held.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlowValidationException(
                $"{flow.LedgerName} holds no dimension named '{name}'{(held.Count == 0 ? string.Empty : $"; it holds {string.Join(", ", held.Select(d => d.Name).Order(StringComparer.Ordinal))}")}.");
        var removed = await DimensionRemoval.RemoveAsync(ledger, dimension, RunActors.LocalAccount(), clock, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimensionId"] = removed.DimensionId,
                ["dimension"] = removed.Name,
                ["flow"] = removed.FlowName,
                ["partition"] = removed.Partition,
                ["values"] = removed.Values,
                ["keys"] = removed.Keys,
                ["builds"] = removed.Builds,
                ["changes"] = removed.Changes,
                ["attributes"] = removed.Attributes,
                ["texts"] = removed.Texts,
                ["tableDropped"] = removed.TableDropped is { } dropped ? DimensionTables.Shown(dropped) : null,
            }));
            return 0;
        }

        context.Out.WriteLine(removed.Describe());
        return 0;
    }

    /// <summary>
    /// The flow's views (osdu/docs/reference/flow/dimension-views.md): each it declares, with whether a build wrote it as
    /// declared and its last check, then each a build of it wrote that it no longer declares; with --view, one view with its
    /// joins, its columns, its newest checks and its SQL.
    /// </summary>
    private static async Task<int> ViewsAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        var recorded = await ledger.ListDimensionViewsAsync(flow.Name, ct).ConfigureAwait(false);
        if (context.Arguments.GetOption("--view") is { } asked && !string.IsNullOrWhiteSpace(asked))
        {
            var name = asked.Trim();
            var declared = flow.Views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            var detail = recorded.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                ? await ledger.GetDimensionViewAsync(name, ct).ConfigureAwait(false)
                : null;
            if (declared is null && detail is null)
            {
                throw new FlowValidationException(
                    $"{flow.Name} declares no view named '{name}', and no build of it wrote one{(flow.Views.Count == 0 ? string.Empty : $"; it declares {string.Join(", ", flow.Views.Select(v => v.Name))}")}.");
            }

            return ShowView(context, declared, detail);
        }

        var rows = flow.Views
            .Select(v => (v.Name, Declared: (DimensionViewSpec?)v, State: recorded.FirstOrDefault(r => string.Equals(r.Name, v.Name, StringComparison.OrdinalIgnoreCase))))
            .Concat(recorded
                .Where(r => flow.Views.All(v => !string.Equals(v.Name, r.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(r => (r.Name, Declared: (DimensionViewSpec?)null, State: (DimensionViewState?)r)))
            .ToList();
        var checks = new Dictionary<string, DimensionViewCheckState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => r.State is not null))
        {
            if ((await ledger.GetDimensionViewAsync(row.Name, ct).ConfigureAwait(false))?.Checks is { Count: > 0 } held)
            {
                checks[row.Name] = held[0];
            }
        }

        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = flow.Name,
                ["views"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["view"] = r.Name,
                    ["viewName"] = DimensionTables.Shown(r.Declared?.ViewName ?? r.State!.ViewName),
                    ["declared"] = r.Declared is not null,
                    ["written"] = r.State?.Written ?? false,
                    ["changed"] = r.Declared is not null && r.State is not null && !string.Equals(r.State.Sql, r.Declared.Definition.CreateSql, StringComparison.Ordinal),
                    ["from"] = r.Declared?.From ?? r.State!.From,
                    ["joins"] = new JsonArray((r.Declared?.Joins.Select(j => j.As) ?? r.State!.Joins.Select(j => j.Alias)).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    ["note"] = r.State?.Note,
                    ["lastCheck"] = checks.TryGetValue(r.Name, out var check) ? CheckJson(check) : null,
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{flow.Name}: {flow.Views.Count} view(s)"));
        foreach (var (name, declared, state) in rows)
        {
            var shown = DimensionTables.Shown(declared?.ViewName ?? state!.ViewName);
            var status = declared is null
                ? "no longer declared: remove it with sqlflow dimensions remove-view <flow.yaml> --view " + name
                : state is null ? "not written yet: a build of the flow writes it"
                : !state.Written ? state.Note ?? "dropped; the next build writes it again"
                : !string.Equals(state.Sql, declared.Definition.CreateSql, StringComparison.Ordinal) ? "changed: the next build writes it again"
                : "written as declared";
            context.Out.WriteLine($"  {name}  {shown}  from {declared?.From ?? state!.From}{Joined(declared, state)}  {status}");
            if (checks.TryGetValue(name, out var check))
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      last check {check.Status} in '{check.Partition}' at {Stamp(check.CheckedUtc)}: {check.Rows} row(s){(check.Error is null ? string.Empty : ". " + check.Error)}"));
                foreach (var note in check.Notes().Where(n => !string.Equals(n, check.Error, StringComparison.Ordinal)))
                {
                    context.Out.WriteLine("      " + note);
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// The joins a view whose rows are <paramref name="from"/>'s could make (<see cref="DimensionViewTemplates.SuggestAsync"/>),
    /// from the flow file's dimensions and the templates of the template folders on its path, as the <c>join:</c> block of a
    /// view's YAML, each with why it is offered: what a person keeps or changes in the file. Nothing is written, and no
    /// database is read.
    /// </summary>
    private static async Task<int> SuggestAsync(CliVerbContext context, DimensionFlowDefinition flow, string from, ITemplateSource templates, CancellationToken ct)
    {
        var dimension = flow.Dimension(from)
            ?? throw new FlowValidationException($"{flow.Name} has no dimension named '{from}'; its dimensions are {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
        var joins = await DimensionViewTemplates.SuggestAsync(flow, dimension.Name, templates, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["from"] = dimension.Name,
                ["joins"] = new JsonArray(joins.Select(j => (JsonNode)new JsonObject { ["on"] = j.On, ["to"] = j.To, ["as"] = j.As, ["note"] = j.Note }).ToArray()),
            }));
            return 0;
        }

        if (joins.Count == 0)
        {
            context.Out.WriteLine($"No join is offered from {dimension.Name}: none of its columns holds a key another dimension of the flow is keyed by, one row a key, of the type its template names.");
            return 0;
        }

        context.Out.WriteLine($"    from: {dimension.Name}");
        context.Out.WriteLine("    join:");
        foreach (var join in joins)
        {
            context.Out.WriteLine($"      - {{ on: {join.On}, to: {join.To}, as: {join.As} }}    # {join.Note}");
        }

        return 0;
    }

    /// <summary>The joins of a view as a line of the list names them.</summary>
    private static string Joined(DimensionViewSpec? declared, DimensionViewState? state)
    {
        var joins = declared?.Joins.Select(j => $"{j.To} as {j.As}").ToList() ?? state?.Joins.Select(j => $"{j.To} as {j.Alias}").ToList() ?? [];
        return joins.Count == 0 ? string.Empty : " joining " + string.Join(", ", joins);
    }

    private static int ShowView(CliVerbContext context, DimensionViewSpec? declared, DimensionViewDetail? detail)
    {
        var state = detail?.View;
        var name = declared?.Name ?? state!.Name;
        var columns = declared?.Definition.Columns.Select(c => new DimensionViewColumnState(c.Name, c.Type, c.Expression, c.DataType, c.Description)).ToList()
            ?? state!.Columns.ToList();
        var joins = declared?.Definition.Joins.Select(j => (j.Alias, j.To, j.On, j.Table)).ToList()
            ?? state!.Joins.Select(j => (j.Alias, j.To, j.On, j.Table)).ToList();
        var checks = detail?.Checks ?? [];
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["view"] = name,
                ["viewName"] = DimensionTables.Shown(declared?.ViewName ?? state!.ViewName),
                ["description"] = declared?.Description ?? state?.Description,
                ["declared"] = declared is not null,
                ["written"] = state?.Written ?? false,
                ["from"] = declared?.From ?? state!.From,
                ["joins"] = new JsonArray(joins.Select(j => (JsonNode)new JsonObject
                {
                    ["alias"] = j.Alias, ["to"] = j.To, ["on"] = j.On, ["table"] = DimensionTables.Shown(j.Table),
                }).ToArray()),
                ["columns"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name, ["type"] = c.Type, ["expression"] = c.Expression, ["dataType"] = c.DataType, ["description"] = c.Description,
                }).ToArray()),
                ["note"] = state?.Note,
                ["checks"] = new JsonArray(checks.Select(c => (JsonNode)CheckJson(c)).ToArray()),
                ["sql"] = state?.Sql,
                ["declaredSql"] = declared?.Definition.CreateSql,
            }));
            return 0;
        }

        context.Out.WriteLine($"{name}  {DimensionTables.Shown(declared?.ViewName ?? state!.ViewName)}  from {declared?.From ?? state!.From}{Joined(declared, state)}");
        if ((declared?.Description ?? state?.Description) is { } description)
        {
            context.Out.WriteLine("  " + description);
        }

        context.Out.WriteLine("  columns:");
        foreach (var column in columns)
        {
            var computed = column.Expression is null ? string.Empty : $" = {column.Expression}";
            context.Out.WriteLine($"    {column.Name} {column.Type}{computed}{(column.Description is null ? string.Empty : "  (" + column.Description + ")")}");
        }

        if (state?.Note is { } note)
        {
            context.Out.WriteLine("  " + note);
        }

        foreach (var check in checks)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  check {check.Status} in '{check.Partition}' at {Stamp(check.CheckedUtc)}: {check.Rows} row(s){(check.Error is null ? string.Empty : ". " + check.Error)}"));
            foreach (var line in check.Notes().Where(n => !string.Equals(n, check.Error, StringComparison.Ordinal)))
            {
                context.Out.WriteLine("    " + line);
            }
        }

        context.Out.WriteLine(state is null ? "  not written yet; a build writes:" : "  as last written:");
        context.Out.WriteLine(state?.Sql ?? declared!.Definition.CreateSql);
        return 0;
    }

    private static JsonObject CheckJson(DimensionViewCheckState check) => new()
    {
        ["partition"] = check.Partition,
        ["runId"] = check.RunId,
        ["status"] = check.Status,
        ["rows"] = check.Rows,
        ["checkedUtc"] = check.CheckedUtc,
        ["reusedFrom"] = check.ReusedFrom,
        ["error"] = check.Error,
        ["joins"] = new JsonArray(check.Joins.Select(j => (JsonNode)new JsonObject
        {
            ["alias"] = j.Alias, ["to"] = j.To, ["on"] = j.On, ["matched"] = j.Matched, ["unmatched"] = j.Unmatched,
            ["examples"] = new JsonArray(j.Examples.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
        }).ToArray()),
        ["unconverted"] = new JsonArray(check.Columns.Where(c => c.Unconverted > 0).Select(c => (JsonNode)new JsonObject
        {
            ["column"] = c.Name, ["values"] = c.Unconverted,
            ["examples"] = new JsonArray(c.Examples.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["value"] = e.Value }).ToArray()),
        }).ToArray()),
        ["notes"] = new JsonArray(check.Notes().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
    };

    /// <summary>
    /// Removes the view --view names, which the flow file no longer declares, for good (<see cref="DimensionViewRemoval"/>):
    /// the view from the database and its record with its checks. One the file declares is refused: its next build would
    /// write it again. The removal is an activity of the flow's ledger, recorded under the operating system user and machine
    /// that ran it.
    /// </summary>
    private static async Task<int> RemoveViewAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, TimeProvider clock, CancellationToken ct)
    {
        if (context.Arguments.GetOption("--view") is not { } asked || string.IsNullOrWhiteSpace(asked))
        {
            return context.UsageError("name the view to remove with --view <name>: one the flow no longer declares.");
        }

        var name = asked.Trim();
        if (flow.Views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) is { } declared)
        {
            throw new FlowValidationException(
                $"{flow.Name} declares view {declared.Name}, so it is not removed: its next build would write it again. Take it out of the flow's file and build the flow, which drops it.");
        }

        var detail = await ledger.GetDimensionViewAsync(name, ct).ConfigureAwait(false);
        if (detail is null || !string.Equals(detail.View.FlowName, flow.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException(
                detail is null
                    ? $"No build wrote a view named '{name}'."
                    : $"View {detail.View.Name} was written by flow {detail.View.FlowName}, not {flow.Name}; remove it with that flow's file.");
        }

        var removed = await DimensionViewRemoval.RemoveAsync(ledger, detail.View, RunActors.LocalAccount(), clock, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["view"] = removed.Name,
                ["viewName"] = DimensionTables.Shown(removed.ViewName),
                ["flow"] = removed.FlowName,
                ["dropped"] = removed.Dropped,
                ["checks"] = removed.Checks,
            }));
            return 0;
        }

        context.Out.WriteLine(removed.Describe());
        return 0;
    }

    /// <summary>Each dimension the flow declares in its partition, with what it holds and its newest build.</summary>
    private static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        var held = (await ledger.ListDimensionsAsync(null, flow.LedgerId, ct).ConfigureAwait(false))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var latest = (await ledger.LatestDimensionRunsAsync(held.Values.Select(d => d.DimensionId).ToList(), ct).ConfigureAwait(false))
            .ToDictionary(r => r.DimensionId);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = flow.Name,
                ["ledger"] = flow.LedgerName,
                ["dimensions"] = new JsonArray(flow.Dimensions.Select(spec =>
                {
                    var dimension = held.GetValueOrDefault(spec.Name);
                    var newest = dimension is not null && latest.TryGetValue(dimension.DimensionId, out var run) ? run : null;
                    return (JsonNode)new JsonObject
                    {
                        ["dimension"] = spec.Name,
                        ["dimensionId"] = dimension?.DimensionId,
                        ["kind"] = spec.Kind,
                        ["path"] = spec.Path,
                        ["label"] = spec.Label.Count == 0 ? null : new JsonArray(spec.Label.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
                        ["attributes"] = new JsonArray(spec.Attributes.Select(a => (JsonNode)(a.IsCollected
                            ? new JsonObject { ["name"] = a.Name, ["collect"] = a.Collect, ["keep"] = DimensionKeeping.Named(a.Keep) }
                            : new JsonObject
                            {
                                ["name"] = a.Name,
                                ["steps"] = new JsonArray(a.Steps.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                                ["keep"] = DimensionKeeping.Named(a.Keep),
                                ["many"] = DimensionKeeping.Named(a.Many),
                                ["decode"] = DimensionKeeping.Named(a.Decode),
                                ["maxReferences"] = a.MaxReferences,
                                ["trim"] = a.Trim,
                            })).ToArray()),
                        ["aggregateBy"] = dimension?.Field?.AggregateBy,
                        ["table"] = dimension?.TableName is { } table ? DimensionTables.Shown(table) : null,
                        ["keyColumn"] = spec.KeyColumn,
                        ["valueColumn"] = spec.ValueColumn,
                        ["values"] = dimension?.Members,
                        ["keys"] = dimension?.Originals,
                        ["lastBuiltUtc"] = dimension?.LastBuiltUtc,
                        ["lastFullLoadUtc"] = dimension?.LastFullBuiltUtc,
                        ["latestBuild"] = newest is null ? null : Described(newest),
                    };
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{flow.LedgerName}: {flow.Dimensions.Count} dimension(s)"));
        foreach (var spec in flow.Dimensions)
        {
            var dimension = held.GetValueOrDefault(spec.Name);
            var label = (spec.Label.Count == 0 ? string.Empty : " labelled by " + string.Join(" > ", spec.Label))
                + $"; key {spec.KeyColumn}, value {spec.ValueColumn}"
                + (spec.Attributes.Count == 0 ? string.Empty : "; attributes " + string.Join(", ", spec.Attributes.Select(a => a.Name)));
            if (dimension is null)
            {
                context.Out.WriteLine($"  {spec.Name}  ({spec.Kind} {spec.Path}{label})  not built yet");
                continue;
            }

            var built = dimension.LastBuiltUtc is { } at ? "built " + Stamp(at) : "not built yet";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {spec.Name}  ({spec.Kind} {spec.Path}{label})  {dimension.Members} value(s) from {dimension.Originals} key(s), {built}"));
            context.Out.WriteLine(dimension.TableName is { } table
                ? $"      table {DimensionTables.Shown(table)}"
                : $"      no table yet: run the flow, or read it with sqlflow dimensions table <flow.yaml> --dimension {spec.Name}");
            if (latest.TryGetValue(dimension.DimensionId, out var newest) && newest.DimensionRunId != dimension.LastRunId)
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      newest build {newest.DimensionRunId} {newest.Status} at {Stamp(newest.StartedUtc)}{(newest.Error is { Length: > 0 } error ? ": " + error : string.Empty)}"));
            }
        }

        if (held.Count == 0)
        {
            context.Out.WriteLine("  none built yet. Build them with: sqlflow run <flow.yaml> (a plan settles each field without reading: --operation plan)");
        }

        return 0;
    }

    /// <summary>
    /// A page of the dimension's table (osdu/docs/reference/flow/dimension-table.md, The dimension's table): a row per key and
    /// value it collects, with a column per attribute; the rows containing --search, holding the attribute values --attr
    /// names, ordered by --order (a column by its name, or the words value and key for those two columns whatever the
    /// dimension names them). The key and its value are written under the names their columns have in the table. A
    /// dimension built before dimensions had a table has its table made here.
    /// </summary>
    private static async Task<int> TableAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        DimensionTablePage page;
        try
        {
            page = await DimensionTable.ReadAsync(
                ledger,
                dimension,
                new DimensionTableQuery(
                    context.Arguments.GetOption("--search"), TableMatches(context), context.Arguments.GetOption("--order"), context.Arguments.HasFlag("--desc"), 0, max),
                ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex) when (ex is not DimensionTableMissingException)
        {
            throw new FlowValidationException(ex.Message, ex);
        }

        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["table"] = page.Table,
                ["keyColumn"] = page.KeyColumn,
                ["valueColumn"] = page.ValueColumn,
                ["total"] = page.Total,
                ["more"] = page.More,
                ["rows"] = new JsonArray(page.Rows.Select(row =>
                {
                    // A row as the table holds it: each column under its own name, the key's and the value's among them.
                    var json = new JsonObject
                    {
                        ["id"] = row.Id, ["partition"] = dimension.Partition, ["key_id"] = row.KeyId, [page.KeyColumn] = row.Key, [page.ValueColumn] = row.Value,
                    };
                    for (var i = 0; i < page.Attributes.Count; i++)
                    {
                        json[page.Attributes[i]] = i < row.Attributes.Count ? row.Attributes[i] : null;
                    }

                    json["records"] = row.Records;
                    json["filter"] = row.Filter;
                    return (JsonNode)json;
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{dimension.Name}: {page.Rows.Count} of {page.Total ?? page.Rows.Count} row(s) of {page.Table}"));
        context.Out.WriteLine($"  {"id",10}  {"records",12}  {page.ValueColumn}  {page.KeyColumn}{(page.Attributes.Count == 0 ? string.Empty : "  " + string.Join("  ", page.Attributes))}");
        foreach (var row in page.Rows)
        {
            var attributes = string.Join("  ", page.Attributes.Select((name, i) => $"{name}={(i < row.Attributes.Count && row.Attributes[i] is { } value ? Quoted(value) : "-")}"));
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {row.Id,10}  {row.Records,12:N0}  {Quoted(row.Value)}  {row.Key}{(attributes.Length == 0 ? string.Empty : "  " + attributes)}"));
        }

        if (page.More)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  more rows follow: raise --max (now {max}), narrow with --search or --attr, or export the table."));
        }

        return 0;
    }

    /// <summary>
    /// The attribute matches --attr asks of the table, each <c>Name=value</c>, named as the table's columns are: the table
    /// says which it has, so a name it lacks is refused with its columns.
    /// </summary>
    private static List<DimensionAttributeMatch>? TableMatches(CliVerbContext context)
    {
        var asked = context.Arguments.GetOptions("--attr");
        if (asked.Count == 0)
        {
            return null;
        }

        var matches = new List<DimensionAttributeMatch>();
        foreach (var text in asked)
        {
            var at = text.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0 || at == text.Length - 1)
            {
                throw new FlowValidationException($"--attr '{text}' is not <attribute>=<value>.");
            }

            var name = text[..at].Trim();
            var index = matches.FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                matches[index] = matches[index] with { Values = [.. matches[index].Values, text[(at + 1)..]] };
            }
            else
            {
                matches.Add(new DimensionAttributeMatch(name, [text[(at + 1)..]]));
            }
        }

        return matches;
    }

    /// <summary>A page of the dimension's values, in value order or with the most records first, each with its commonest keys.</summary>
    private static async Task<int> ValuesAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var order = context.Arguments.GetOption("--order")?.Trim().ToLowerInvariant() switch
        {
            null or "value" => DimensionMemberOrder.Value,
            "records" => DimensionMemberOrder.Records,
            var other => throw new FlowValidationException($"--order '{other}' is not one of value, records."),
        };
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var matches = AttributeMatches(context, dimension);
        var members = await ledger.ListDimensionMembersAsync(
            dimension.DimensionId,
            new DimensionMemberQuery(context.Arguments.GetOption("--search"), context.Arguments.HasFlag("--removed"), null, max, order, matches),
            ct).ConfigureAwait(false);
        var top = (await ledger.TopMemberOriginalsAsync(dimension.DimensionId, members.Select(m => m.MemberId).ToList(), ShownKeys, ct).ConfigureAwait(false))
            .GroupBy(v => v.MemberId ?? 0)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Count).ThenBy(v => v.ValueId).Select(v => v.Original).ToList());
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["values"] = new JsonArray(members.Select(m => (JsonNode)new JsonObject
                {
                    ["valueId"] = m.MemberId,
                    ["value"] = m.Value,
                    ["records"] = m.Records,
                    ["recordsExact"] = m.RecordsExact,
                    ["keys"] = m.Originals,
                    ["unfilterable"] = m.Unfilterable,
                    ["filter"] = m.Filter,
                    ["filterParts"] = m.FilterParts,
                    ["removedUtc"] = m.RemovedUtc,
                    ["top"] = new JsonArray((top.GetValueOrDefault(m.MemberId) ?? []).Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
                    ["attributes"] = new JsonArray(m.Attributes.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["value"] = a.Value, ["keys"] = a.Keys }).ToArray()),
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {members.Count} of {dimension.Members} value(s)"));
        foreach (var member in members)
        {
            var records = member.RecordsExact ? member.Records.ToString("N0", CultureInfo.InvariantCulture) : "~" + member.Records.ToString("N0", CultureInfo.InvariantCulture);
            var shown = top.GetValueOrDefault(member.MemberId) ?? [];
            var more = member.Originals > shown.Count ? string.Create(CultureInfo.InvariantCulture, $", {member.Originals - shown.Count} more") : string.Empty;
            var removed = member.RemovedUtc is { } gone ? "  (no build finds it since " + Stamp(gone) + ")" : string.Empty;
            var attributes = member.Attributes.Count == 0
                ? string.Empty
                : "  [" + string.Join("; ", member.Attributes.GroupBy(a => a.Name).Select(g => $"{g.Key}: {string.Join(", ", g.Select(a => a.Value))}")) + "]";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {records,12}  {member.Value}  <- {string.Join(", ", shown.Select(Quoted))}{more}{removed}{attributes}"));
        }

        return 0;
    }

    /// <summary>
    /// A page of the dimension's keys, each exactly as the index holds it with its label and filter: every one, a value's, or
    /// those of no value, in arrival order or most records first.
    /// </summary>
    private static async Task<int> KeysAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var order = context.Arguments.GetOption("--order")?.Trim().ToLowerInvariant() switch
        {
            null or "arrival" => DimensionValueOrder.Arrival,
            "count" => DimensionValueOrder.Count,
            var other => throw new FlowValidationException($"--order '{other}' is not one of arrival, count."),
        };
        var leftOut = context.Arguments.HasFlag("--left-out");
        long? memberId = null;
        if (context.Arguments.GetOption("--value") is { } named)
        {
            if (leftOut)
            {
                return context.UsageError("a key of a value is not left out: give --value or --left-out, not both.");
            }

            memberId = (await ValuesNamedAsync(ledger, dimension, [named], ct).ConfigureAwait(false))[0].MemberId;
        }

        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var keys = await ledger.ListDimensionValuesAsync(
            dimension.DimensionId,
            new DimensionValueQuery(
                context.Arguments.GetOption("--search"), memberId, leftOut, context.Arguments.HasFlag("--removed"), null, max, order, AttributeMatches(context, dimension)),
            ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["keys"] = new JsonArray(keys.Select(k => (JsonNode)new JsonObject
                {
                    ["keyId"] = k.ValueId,
                    ["key"] = k.Original,
                    ["label"] = k.Label,
                    ["labelFrom"] = k.LabelFrom,
                    ["value"] = k.MemberValue,
                    ["leftOut"] = k.LeftOut,
                    ["note"] = k.Note,
                    ["count"] = k.Count,
                    ["filterable"] = k.Filterable,
                    ["filter"] = k.Filter,
                    ["attributes"] = KeyAttributesJson(dimension, k.Attributes),
                    ["removedUtc"] = k.RemovedUtc,
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {keys.Count} of {dimension.Originals} key(s)"));
        foreach (var key in keys)
        {
            var of = key.MemberValue is { } member ? "-> " + member : "of no value (" + key.LeftOut + ")";
            var label = key.Label is { } read && !string.Equals(read, key.MemberValue, StringComparison.Ordinal) ? "  label " + Quoted(read) : string.Empty;
            var unfilterable = key.Filterable ? string.Empty : "  (no query can carry it)";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {key.Count,12:N0}  {Quoted(key.Original)}  {of}{label}{unfilterable}{(key.Note is { Length: > 0 } note ? "  " + note : string.Empty)}"));
            if (key.Attributes.Count > 0)
            {
                context.Out.WriteLine("                " + string.Join("; ", key.Attributes.GroupBy(a => a.Name).Select(g => $"{g.Key}: {string.Join(", ", g.Select(Collected))}")));
            }

            if (key.Filter is { } filter)
            {
                context.Out.WriteLine("                search " + filter);
            }
        }

        return 0;
    }

    /// <summary>The search that finds the records of the values --value names, one query a line, ready to send.</summary>
    private static async Task<int> FilterAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var named = context.Arguments.GetOptions("--value");
        if (named.Count == 0)
        {
            return context.UsageError("name the values to filter by, exactly as the dimension holds them: --value <value> (repeat it for more).");
        }

        var filter = await DimensionFilters.ForMembersAsync(ledger, dimension, [], named, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["kind"] = filter.Kind,
                ["query"] = filter.Query,
                ["aggregateBy"] = filter.AggregateBy,
                ["filters"] = new JsonArray(filter.Filters.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
                ["searches"] = new JsonArray(filter.Searches.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["values"] = new JsonArray(filter.Members.Select(m => (JsonNode)JsonValue.Create(m.Value)!).ToArray()),
                ["keys"] = filter.Originals,
                ["unfilterable"] = filter.Unfilterable,
                ["removed"] = new JsonArray(filter.Removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["missing"] = new JsonArray(filter.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            }));
            return filter.Searches.Count == 0 ? 1 : 0;
        }

        // The searches go to the console alone, a line each, so a script can take them as they are; what the filter covers and
        // leaves out goes to the error stream.
        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{dimension.Name}: kind {filter.Kind}, {filter.Members.Count} value(s), {filter.Originals} key(s) in {filter.Searches.Count} search(es)"));
        if (filter.Unfilterable > 0)
        {
            context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {filter.Unfilterable} key(s) no query can carry are left out: {string.Join(", ", filter.UnfilterableNamed.Take(5).Select(Quoted))}"));
        }

        if (filter.Removed.Count > 0)
        {
            context.Error.WriteLine("  no build finds these any more: " + string.Join(", ", filter.Removed.Select(Quoted)));
        }

        if (filter.Missing.Count > 0)
        {
            context.Error.WriteLine("  no value of the dimension: " + string.Join(", ", filter.Missing.Select(Quoted)));
        }

        foreach (var search in filter.Searches)
        {
            context.Out.WriteLine(search);
        }

        return filter.Searches.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// The search that finds the records holding one of the values picked in each dimension --pick names: OR within a
    /// dimension, AND across them, in the kind every dimension reads or the one --kind names, narrowed by --within. The query
    /// goes to the console alone, so a script takes it as it is; with --json, the whole composition with the request to send.
    /// </summary>
    private static async Task<int> SearchAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        var given = context.Arguments.GetOptions("--pick");
        var where = context.Arguments.GetOptions("--where");
        if (given.Count + where.Count == 0)
        {
            return context.UsageError(
                "pick what to search for: --pick <dimension>=<value>, or --where <dimension>.<attribute>=<value> (repeat either for more).");
        }

        // A pick is split at its first '=': a dimension's name holds none, and a value may.
        var byDimension = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pick in given)
        {
            var at = pick.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                return context.UsageError($"--pick '{pick}' is not <dimension>=<value>.");
            }

            var name = pick[..at].Trim();
            (byDimension.TryGetValue(name, out var values) ? values : byDimension[name] = []).Add(pick[(at + 1)..]);
        }

        // A where is split at its first '=', and what is before it at its last '.': an attribute's name holds no dot.
        var byAttribute = new Dictionary<string, List<DimensionAttributeMatch>>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in where)
        {
            var equals = condition.IndexOf('=', StringComparison.Ordinal);
            var dot = equals <= 0 ? -1 : condition.LastIndexOf('.', equals - 1);
            if (equals <= 0 || dot <= 0 || dot == equals - 1)
            {
                return context.UsageError($"--where '{condition}' is not <dimension>.<attribute>=<value>.");
            }

            var dimensionName = condition[..dot].Trim();
            (byAttribute.TryGetValue(dimensionName, out var list) ? list : byAttribute[dimensionName] = [])
                .Add(new DimensionAttributeMatch(condition[(dot + 1)..equals].Trim(), [condition[(equals + 1)..]]));
            byDimension.TryAdd(dimensionName, []);
        }

        var picks = new List<DimensionPick>(byDimension.Count);
        foreach (var (name, values) in byDimension)
        {
            var spec = flow.Dimension(name)
                ?? throw new FlowValidationException($"{flow.Name} declares no dimension named '{name}'; it declares {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
            var dimension = await ledger.FindDimensionAsync(flow.LedgerId, spec.Name, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"Dimension {spec.Name} has not been built in {flow.LedgerName} yet. Build it with: sqlflow run <flow.yaml> --payload '{{\"dimensions\":[\"{spec.Name}\"]}}'");
            picks.Add(new DimensionPick(dimension, [], values.Distinct(StringComparer.Ordinal).ToList(), byAttribute.GetValueOrDefault(name)));
        }

        var set = await DimensionSearch.ComposeAsync(ledger, picks, context.Arguments.GetOption("--kind"), context.Arguments.GetOption("--within"), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["kind"] = set.Kind,
                ["query"] = set.Query,
                ["request"] = new JsonObject { ["kind"] = set.Kind, ["query"] = set.Query, ["limit"] = 1000 },
                ["clauses"] = set.Clauses,
                ["parts"] = new JsonArray(set.Parts.Select(p => (JsonNode)new JsonObject
                {
                    ["dimension"] = p.Dimension,
                    ["aggregateBy"] = p.AggregateBy,
                    ["values"] = new JsonArray(p.Values.Select(v => (JsonNode)JsonValue.Create(v.Value)!).ToArray()),
                    ["keys"] = p.Keys,
                    ["unfilterable"] = p.Unfilterable,
                    ["filter"] = p.Filter,
                    ["query"] = p.Query,
                    ["attributes"] = new JsonArray(p.Attributes.Select(a => (JsonNode)new JsonObject
                    {
                        ["name"] = a.Name,
                        ["values"] = new JsonArray(a.Values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                    }).ToArray()),
                }).ToArray()),
                ["removed"] = new JsonArray(set.Removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["missing"] = new JsonArray(set.Missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                ["notes"] = new JsonArray(set.Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            }));
            return 0;
        }

        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"kind {set.Kind}, {set.Parts.Count} dimension(s), {set.Clauses} clause(s)"));
        foreach (var part in set.Parts)
        {
            var by = part.Attributes.Count == 0 ? string.Empty : " where " + string.Join(" and ", part.Attributes.Select(a => $"{a.Name} is {string.Join(" or ", a.Values.Select(Quoted))}"));
            context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {part.Dimension}{by}: {string.Join(", ", part.Values.Take(10).Select(v => Quoted(v.Value)))}{(part.Values.Count > 10 ? ", ..." : string.Empty)} ({part.Keys} key(s) of {part.AggregateBy})"));
        }

        foreach (var line in set.Removed.Select(r => "  no build finds this any more: " + r)
                     .Concat(set.Missing.Select(m => "  no value of the dimension: " + m))
                     .Concat(set.Notes.Select(n => "  " + n)))
        {
            context.Error.WriteLine(line);
        }

        context.Out.WriteLine(set.Query);
        return 0;
    }

    /// <summary>The dimension's builds, newest first: what each came to, how it read, and what it changed.</summary>
    private static async Task<int> HistoryAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var runs = await ledger.ListDimensionRunsAsync(dimension.DimensionId, max, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["builds"] = new JsonArray(runs.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {runs.Count} build(s)"));
        foreach (var run in runs)
        {
            var changes = run.Changes;
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.DimensionRunId,8}  {run.Status,-9}  {Stamp(run.StartedUtc)}  {Loaded(run.Read)}  {run.Members} value(s) from {run.Originals} key(s), {run.LeftOut} of none; {changes.OriginalsAdded} arrived, {changes.OriginalsRemoved} left, {changes.OriginalsMoved} moved, {changes.OriginalsRestored} came back; {run.Read.Aggregations} aggregation(s), {run.Read.Splits} split(s), {run.Read.ScanPages} scan page(s), {run.Read.Labelled} labelled in {run.Read.LabelQueries} search(es)  by {run.Actor}"));
            if (run.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("            " + error);
            }

            foreach (var note in run.Read.Notes.Take(5))
            {
                context.Out.WriteLine("            " + note);
            }
        }

        return 0;
    }

    /// <summary>The change log, newest first: of every build, of the one --build names, of one value, or of one kind of change.</summary>
    private static async Task<int> ChangesAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        long? build = null;
        if (context.Arguments.GetOption("--build") is { } asked)
        {
            build = long.TryParse(asked, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : throw new FlowValidationException($"--build '{asked}' is not a build number.");
        }

        var change = context.Arguments.GetOption("--change")?.Trim().ToLowerInvariant();
        if (change is not null && change is not (DimensionChangeKinds.Added or DimensionChangeKinds.Removed or DimensionChangeKinds.Moved or DimensionChangeKinds.Restored))
        {
            return context.UsageError($"--change '{change}' is not one of added, removed, moved, restored.");
        }

        long? memberId = null;
        if (context.Arguments.GetOption("--value") is { } value)
        {
            memberId = (await ValuesNamedAsync(ledger, dimension, [value], ct).ConfigureAwait(false))[0].MemberId;
        }

        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var changes = await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(build, null, memberId, change, null, max), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["changes"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject
                {
                    ["changeId"] = c.ChangeId,
                    ["build"] = c.DimensionRunId,
                    ["key"] = c.Original,
                    ["change"] = c.Change,
                    ["from"] = c.FromValue,
                    ["to"] = c.ToValue,
                    ["changedUtc"] = c.ChangedUtc,
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}: {changes.Count} change(s)"));
        foreach (var c in changes)
        {
            var move = c.Change switch
            {
                DimensionChangeKinds.Moved => $"from {c.FromValue ?? "no value"} to {c.ToValue ?? "no value"}",
                DimensionChangeKinds.Removed => $"left {c.FromValue ?? "no value"}",
                _ => $"to {c.ToValue ?? "no value"}",
            };
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Stamp(c.ChangedUtc)}  build {c.DimensionRunId,-6}  {c.Change,-8}  {Quoted(c.Original)}  {move}"));
        }

        return 0;
    }

    /// <summary>
    /// A key's attributes as JSON: an attribute read from the record the key names is one value, a collected one a list of
    /// its values, each with the records holding it.
    /// </summary>
    private static JsonObject KeyAttributesJson(DimensionState dimension, IReadOnlyList<DimensionAttributeState> attributes)
    {
        var collected = DimensionRunner.AttributesOf(dimension.AttributesJson).Where(a => a.IsCollected).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var json = new JsonObject();
        foreach (var group in attributes.GroupBy(a => a.Name))
        {
            json[group.Key] = collected.Contains(group.Key)
                ? new JsonArray(group.Select(a => (JsonNode)new JsonObject { ["value"] = a.Value, ["records"] = a.Records }).ToArray())
                : JsonValue.Create(group.First().Value);
        }

        return json;
    }

    /// <summary>An attribute value as a key's line shows it: a collected one with how many of the key's records hold it.</summary>
    private static string Collected(DimensionAttributeState attribute)
        => attribute.Records is { } records ? string.Create(CultureInfo.InvariantCulture, $"{attribute.Value} ({records:N0})") : attribute.Value;

    /// <summary>The dimension's values, keys or table, whole, as CSV or JSON Lines, to --out or the console.</summary>
    private static async Task<int> ExportAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var set = DimensionExport.SetOf(context.Arguments.GetOption("--set"))
            ?? throw new FlowValidationException($"--set '{context.Arguments.GetOption("--set")}' is not one of values, keys, table.");
        var format = DimensionExport.FormatOf(context.Arguments.GetOption("--format"))
            ?? throw new FlowValidationException($"--format '{context.Arguments.GetOption("--format")}' is not one of csv, jsonl.");
        if (context.Arguments.GetOption("--out") is not { } outPath)
        {
            await DimensionExport.WriteAsync(ledger, dimension, set, format, context.Out, ct).ConfigureAwait(false);
            await context.Out.FlushAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var full = Path.GetFullPath(outPath);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside its final name and moved into place, so a failure part way never leaves half an export under it.
        var partial = full + ".partial";
        long rows;
        try
        {
            var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                rows = await DimensionExport.WriteAsync(ledger, dimension, set, format, file, ct).ConfigureAwait(false);
            }

            File.Move(partial, full, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        var what = set switch { DimensionExportSet.Values => "value(s)", DimensionExportSet.Keys => "key(s)", _ => "row(s) of the table" };
        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {rows} {what} of {dimension.Name} to {full}"));
        return 0;
    }

    /// <summary>
    /// The values the attribute --attribute names holds among the dimension's keys, the most records first, those containing
    /// --search when given: among the keys holding every other attribute value --attr names and belonging to a value --value
    /// names, so each select of a cascade lists what the other picks leave. An --attr of the attribute itself does not
    /// narrow its own list.
    /// </summary>
    private static async Task<int> AttributesAsync(CliVerbContext context, ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        if (context.Arguments.GetOption("--attribute") is not { } asked || string.IsNullOrWhiteSpace(asked))
        {
            return context.UsageError(declared.Count == 0
                ? $"name the attribute with --attribute <name>; dimension {dimension.Name} reads none."
                : $"name the attribute with --attribute <name>; dimension {dimension.Name} reads {string.Join(", ", declared.Select(a => a.Name))}.");
        }

        var attribute = declared.FirstOrDefault(a => string.Equals(a.Name, asked.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new FlowValidationException(
                $"Dimension {dimension.Name} reads no attribute '{asked}'{(declared.Count == 0 ? "; it reads none" : $"; it reads {string.Join(", ", declared.Select(a => a.Name))}")}.");
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var matches = AttributeMatches(context, dimension);
        var named = context.Arguments.GetOptions("--value");
        var memberIds = named.Count == 0 ? null : (await ValuesNamedAsync(ledger, dimension, named, ct).ConfigureAwait(false)).Select(m => m.MemberId).ToList();
        var values = await ledger.ListDimensionAttributeValuesAsync(
            dimension.DimensionId, new DimensionAttributeValueQuery(attribute.Name, context.Arguments.GetOption("--search"), max, matches, memberIds), ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["dimension"] = dimension.Name,
                ["attribute"] = attribute.Name,
                ["collect"] = attribute.Collect,
                ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["value"] = v.Value, ["keys"] = v.Keys, ["records"] = v.Records }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{dimension.Name}.{attribute.Name}: {values.Count} value(s)"));
        foreach (var value in values)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {value.Records,12:N0}  {Quoted(value.Value)}  ({value.Keys} key(s))"));
        }

        return 0;
    }

    /// <summary>
    /// The attribute matches --attr asks for, each <c>Name=value</c> (the value everything after the first '='), named as
    /// the dimension declares them: several of one attribute are any of their values, several attributes all of them.
    /// </summary>
    private static List<DimensionAttributeMatch>? AttributeMatches(CliVerbContext context, DimensionState dimension)
    {
        var asked = context.Arguments.GetOptions("--attr");
        if (asked.Count == 0)
        {
            return null;
        }

        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        var matches = new List<DimensionAttributeMatch>();
        foreach (var text in asked)
        {
            var at = text.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0 || at == text.Length - 1)
            {
                throw new FlowValidationException($"--attr '{text}' is not <attribute>=<value>.");
            }

            var name = text[..at].Trim();
            var attribute = declared.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FlowValidationException(
                    $"Dimension {dimension.Name} reads no attribute '{name}'{(declared.Count == 0 ? "; it reads none" : $"; it reads {string.Join(", ", declared.Select(a => a.Name))}")}.");
            var index = matches.FindIndex(m => m.Name == attribute.Name);
            if (index >= 0)
            {
                matches[index] = matches[index] with { Values = [.. matches[index].Values, text[(at + 1)..]] };
            }
            else
            {
                matches.Add(new DimensionAttributeMatch(attribute.Name, [text[(at + 1)..]]));
            }
        }

        return matches;
    }

    /// <summary>The dimension --dimension names, as the flow's ledger in its partition holds it.</summary>
    private static async Task<DimensionState> DimensionAsync(CliVerbContext context, ILedger ledger, DimensionFlowDefinition flow, CancellationToken ct)
    {
        if (context.Arguments.GetOption("--dimension") is not { } name || string.IsNullOrWhiteSpace(name))
        {
            throw new FlowValidationException(
                $"Name the dimension with --dimension <name>; {flow.Name} declares {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
        }

        var spec = flow.Dimension(name)
            ?? throw new FlowValidationException($"{flow.Name} declares no dimension named '{name}'; it declares {string.Join(", ", flow.Dimensions.Select(d => d.Name))}.");
        return await ledger.FindDimensionAsync(flow.LedgerId, spec.Name, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException(
                $"Dimension {spec.Name} has not been built in {flow.LedgerName} yet. Build it with: sqlflow run <flow.yaml> --payload '{{\"dimensions\":[\"{spec.Name}\"]}}'");
    }

    /// <summary>The values named, each one the dimension holds, in the order named.</summary>
    private static async Task<IReadOnlyList<DimensionMemberState>> ValuesNamedAsync(ILedger ledger, DimensionState dimension, IReadOnlyList<string> values, CancellationToken ct)
    {
        var found = await ledger.GetDimensionMembersAsync(dimension.DimensionId, [], values, ct).ConfigureAwait(false);
        return values.Select(v => found.FirstOrDefault(m => string.Equals(m.Value, v, StringComparison.Ordinal))
                ?? throw new FlowValidationException($"Dimension {dimension.Name} has no value '{v}'. Values are named exactly as the dimension holds them."))
            .ToList();
    }

    private static JsonObject Described(DimensionRunState run) => new()
    {
        ["build"] = run.DimensionRunId,
        ["runId"] = run.RunId?.ToString("D"),
        ["status"] = run.Status,
        ["values"] = run.Members,
        ["keys"] = run.Originals,
        ["leftOut"] = run.LeftOut,
        ["unfilterable"] = run.Unfilterable,
        ["keysAdded"] = run.Changes.OriginalsAdded,
        ["keysRemoved"] = run.Changes.OriginalsRemoved,
        ["keysMoved"] = run.Changes.OriginalsMoved,
        ["keysRestored"] = run.Changes.OriginalsRestored,
        ["labelled"] = run.Read.Labelled,
        ["unlabelled"] = run.Read.Unlabelled,
        ["labelQueries"] = run.Read.LabelQueries,
        ["records"] = run.Read.Records,
        ["withValue"] = run.Read.WithValue,
        ["aggregations"] = run.Read.Aggregations,
        ["splits"] = run.Read.Splits,
        ["scanPages"] = run.Read.ScanPages,
        ["load"] = run.Read.Mode,
        ["windowFromUtc"] = run.Read.WindowFrom,
        ["windowToUtc"] = run.Read.WindowTo,
        ["changedRecords"] = run.Read.ChangedRecords,
        ["touchedKeys"] = run.Read.TouchedKeys,
        ["actor"] = run.Actor,
        ["startedUtc"] = run.StartedUtc,
        ["completedUtc"] = run.CompletedUtc,
        ["error"] = run.Error,
    };

    /// <summary>How a build loaded its dimension, as a line shows it: in full, or the window and what an incremental load read again.</summary>
    private static string Loaded(DimensionReadCounts read)
        => read.Mode == DimensionRunModes.Incremental
            ? string.Create(CultureInfo.InvariantCulture,
                $"incremental [{(read.WindowFrom is { } from ? Stamp(from) : "*")}, {(read.WindowTo is { } to ? Stamp(to) : "*")}): {read.ChangedRecords?.ToString(CultureInfo.InvariantCulture) ?? "?"} record(s) changed, {read.TouchedKeys?.ToString(CultureInfo.InvariantCulture) ?? "?"} key(s) read again;")
            : "full;";

    private static int Count(string? value, int fallback, string option)
    {
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= OsduLedger.MaxDimensionPage
            ? count
            : throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{option} '{value}' is not a count between 1 and {OsduLedger.MaxDimensionPage}."));
    }

    /// <summary>A value as a line shows it: quoted, so an empty value or one with spaces at its ends reads as it is.</summary>
    private static string Quoted(string value) => "\"" + value + "\"";

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
