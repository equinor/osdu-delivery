using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The OSDU module's own verbs: <c>check</c> (everything checkable for one flow: its documents, the mapping against its
/// pinned template, the version of the partition cache it reads, the roots its payload files may sit under, and, with
/// <c>--connect</c>, the ingestion tables themselves), <c>cache</c> (list the versions of a partition's cache, or import a
/// cache flow's type files into it for offline work), and <c>template</c> (capture, import, list, show and delete the
/// templates in the module's database, osdu/docs/reference/concepts/templates.md). A cache is captured from OSDU by
/// running a cache flow (<c>sqlflow run &lt;cache.yaml&gt;</c>), the same run the platform schedules.
/// </summary>
internal static class DeliveryVerbs
{
    /// <summary>Child datasets a connected check reports the columns of before it summarises the rest.</summary>
    private const int MaxDatasetsShown = 20;

    public static async Task<int> CheckAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Arguments.Positional(1) is not { } flowPath)
        {
            return context.UsageError("name the flow document to check.");
        }

        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var values = RunParameters.ParseValues(context.Arguments.GetOptions("--set"));
        var connect = context.Arguments.HasFlag("--connect");
        // A flow that works in partitions is checked in the one --partition names, or in the one a run would take.
        var source = await CliPartitions.ResolveAsync(context, engine.Documents.LoadSource(flowPath), ct).ConfigureAwait(false);
        var named = context.Arguments.GetOption("--interface");
        var flows = named is null ? source.Interfaces : [source.Interface(named)];

        var checks = new List<FlowCheck>(flows.Count);
        foreach (var flow in flows)
        {
            checks.Add(await CheckFlowAsync(context, engine, flow, values, connect, ct).ConfigureAwait(false));
        }

        if (!source.DeclaresInterfaces)
        {
            // The single form reports as it always has: one flow, one object.
            var single = checks[0];
            if (context.Json)
            {
                context.Out.WriteLine(CanonicalJson.Pretty(single.Result));
                return 0;
            }

            single.Write(null);
            return 0;
        }

        // The order of the checked interfaces, from after: and the relationships their mappings fill, as a run orders them.
        Model.InterfaceOrderPlan order;
        try
        {
            order = Model.InterfaceOrder.Plan(
                checks.Select(c => c.Schema.Interface).ToList(), Model.InterfaceOrder.Declared(source), checks.Select(c => c.Schema).ToList());
        }
        catch (DeliveryException ex)
        {
            throw new FlowValidationException($"{source.SourcePath ?? flowPath}: {ex.Message}", ex);
        }
        if (context.Json)
        {
            foreach (var check in checks)
            {
                var name = check.Schema.Interface;
                check.Result["wave"] = order.WaveOf(name);
                check.Result["waitsFor"] = Dependencies(order.WaitsFor(name), d => d.DependsOn);
                check.Result["notWaitedFor"] = Dependencies(order.NotWaitedFor.Where(d => Same(d.Interface, name)).ToList(), d => d.DependsOn);
            }

            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = source.Name,
                ["order"] = order.Describe(),
                ["interfaces"] = new JsonArray(checks.Select(c => (JsonNode)c.Result).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{source.Name}: {checks.Count} of {source.Interfaces.Count} interface(s) checked, in the order they run: {order.Describe()}"));
        foreach (var check in checks)
        {
            check.Write(order);
        }

        return 0;
    }

    /// <summary>What one flow (one interface of a source) answers as JSON, how it writes itself as text, and what it refers to.</summary>
    private sealed record FlowCheck(JsonObject Result, Action<Model.InterfaceOrderPlan?> Write, Model.InterfaceSchema Schema);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static JsonArray Dependencies(IReadOnlyList<Model.InterfaceDependency> dependencies, Func<Model.InterfaceDependency, string> other)
        => new(dependencies
            .Select(d => (JsonNode)new JsonObject
            {
                ["interface"] = other(d),
                ["origin"] = d.Origin == Model.DependencyOrigin.After ? "after" : "schema",
                ["why"] = d.Why,
            })
            .ToArray());

    /// <summary>
    /// Everything checkable for one flow (one interface of a source): what the check answers as JSON, how it writes it as
    /// text once the order of the checked interfaces is known, and what its records refer to. The runtime is opened and
    /// disposed here, so a source's interfaces are checked one at a time.
    /// </summary>
    private static async Task<FlowCheck> CheckFlowAsync(
        CliVerbContext context, EngineContext engine, Model.FlowDefinition flow, IReadOnlyDictionary<string, string> values, bool connect, CancellationToken ct)
    {
        using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow, values, ct).ConfigureAwait(false);
        RouteChecks.Check(flow, runtime.Mapping.Mapping.Kind);
        var schema = await runtime.SchemaAsync(ct).ConfigureAwait(false);
        var roots = PayloadRoots.Of(flow, runtime.Parameters);

        var result = new JsonObject
        {
            ["flow"] = flow.Name,
            ["flowId"] = flow.Id.ToString("D"),
            ["mapping"] = runtime.Mapping.Mapping.Reference,
            ["template"] = new JsonObject
            {
                ["kind"] = runtime.Mapping.Mapping.Template.Kind,
                ["version"] = runtime.Mapping.Mapping.Template.Version,
                ["file"] = runtime.Mapping.TemplateOrigin,
                ["versionNamed"] = runtime.Mapping.Mapping.TemplateVersionNamed,
            },
            ["renderContext"] = JsonNode.Parse(runtime.Mapping.Context.Canonical()),
            ["source"] = new JsonObject
            {
                ["connection"] = flow.Source.Connection,
                ["record"] = flow.Source.Record.Object,
                ["key"] = new JsonArray(flow.Source.Record.Key.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
                ["datasets"] = new JsonArray(flow.Source.Datasets
                    .OrderBy(d => d.Key, StringComparer.Ordinal)
                    .Select(d => (JsonNode)new JsonObject { ["name"] = d.Key, ["object"] = d.Value.Object })
                    .ToArray()),
                ["payloadRoots"] = new JsonArray(roots.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
            },
            ["mappings"] = runtime.Layout.MappingsDirectory,
            ["cache"] = runtime.Mapping.Context.CacheScope is { } cacheScope
                ? new JsonObject
                {
                    ["partition"] = cacheScope,
                    ["version"] = runtime.Mapping.References.Version,
                    ["types"] = runtime.Mapping.References.Types.Count,
                }
                : null,
        };

        if (flow.Interface is { } interfaceName)
        {
            result["interface"] = interfaceName;
            result["ledger"] = flow.LedgerName;
            result["route"] = new JsonObject
            {
                ["name"] = DeliveryProtocols.Name(flow.Target.Protocol),
                ["reason"] = flow.RouteReason,
            };
            result["after"] = new JsonArray(flow.After.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
        }

        // Where a ddms flow's records go is worked out from what the flow declares; a DDMS it names by registration is
        // read from the Register service when the flow runs, not by a check.
        var ddms = Protocols.DeliveryProtocols.ReachesDdms(flow.Target.Protocol) ? DdmsRouting.Of(flow).Explain(runtime.Mapping.Mapping.Kind) : null;
        if (ddms is not null)
        {
            result["ddms"] = ddms;
        }

        if (connect)
        {
            result["read"] = await ReadAsync(engine, runtime, ct).ConfigureAwait(false);
        }

        // What the text form needs is captured now: the runtime is gone by the time it writes.
        var reference = runtime.Mapping.Mapping.Reference;
        var template = runtime.Mapping.Mapping.Template.ToString();
        var templateFrom = runtime.Mapping.TemplateOrigin is { } origin
            ? $"{origin}{(runtime.Mapping.Mapping.TemplateVersionNamed ? string.Empty : ", the version its folder holds")}"
            : "a template folder on the mapping's path";
        var cacheLine = runtime.Mapping.Context.CacheScope is { } scope
            ? $"    cache       partition {scope} version {runtime.Mapping.References.Version} ({runtime.Mapping.References.Types.Count} type(s))"
            : "    cache       none (the mapping reads nothing from a cache)";
        var searchLine = runtime.Mapping.Mapping.Searches.Count == 0 ? null : SearchLine(runtime.Mapping.Context);
        var contextHash = runtime.Mapping.Context.Hash()[..16];
        var mappings = runtime.Layout.MappingsDirectory;

        void Write(Model.InterfaceOrderPlan? order)
        {
            context.Out.WriteLine($"OK  {flow.Label} ({flow.Id:D})");
            if (flow.Interface is { } name && order is not null)
            {
                context.Out.WriteLine($"    ledger      {flow.LedgerName}");
                context.Out.WriteLine($"    route       {DeliveryProtocols.Name(flow.Target.Protocol)}: {flow.RouteReason}");
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    wave        {order.WaveOf(name)}"));
                var waits = order.WaitsFor(name);
                if (waits.Count == 0)
                {
                    context.Out.WriteLine("    waits for   nothing");
                }

                foreach (var wait in waits)
                {
                    context.Out.WriteLine($"    waits for   {wait.DependsOn}: {wait.Why}");
                }

                foreach (var reference in order.NotWaitedFor.Where(d => Same(d.Interface, name)))
                {
                    context.Out.WriteLine($"    not waited  {reference.DependsOn}: {reference.Why}");
                }
            }

            if (ddms is not null)
            {
                context.Out.WriteLine($"    ddms        {ddms}");
            }

            context.Out.WriteLine($"    mapping     {reference}");
            context.Out.WriteLine($"    template    {template} ({templateFrom})");
            context.Out.WriteLine(cacheLine);
            if (searchLine is not null)
            {
                context.Out.WriteLine(searchLine);
            }

            context.Out.WriteLine($"    context     {contextHash}");
            context.Out.WriteLine($"    mappings    {mappings}");
            context.Out.WriteLine(
                $"    source      {flow.Source.Connection} {flow.Source.Record.Object}"
                + (flow.Source.Datasets.Count == 0 ? string.Empty : $" (+{flow.Source.Datasets.Count} dataset(s))"));
            context.Out.WriteLine(roots.Count == 0
                ? "    payloads    none (the flow streams no payload files)"
                : $"    payloads    under {string.Join(", ", roots)}");
            if (result["read"] is JsonObject read)
            {
                WriteRead(context, read);
            }
            else
            {
                context.Out.WriteLine("    tables      not read (run with --connect to open the ingestion tables)");
            }
        }

        return new FlowCheck(result, Write, schema);
    }

    /// <summary>
    /// Opens the flow's ingestion tables on this machine, through the flow's own connection reference, the way a run opens
    /// them (<see cref="Planner.OpenAsync"/>), and reports what the next run would read now: the tables and their columns,
    /// the key columns with the SQL types the window and key parameters are typed by, the flow's system columns, the window
    /// the run would read (<see cref="ScopeReads"/>) and why, and how many records are in it. The mapping is checked against
    /// the tables as a run checks it (every dataset column, label column and child dataset it reads), so a mapping that
    /// reads a column the table lacks fails here, naming the column. Nothing is planned, rendered or delivered.
    /// </summary>
    internal static async Task<JsonObject> ReadAsync(EngineContext engine, FlowRuntime runtime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runtime);
        var flow = runtime.Flow;
        var read = await ScopeReads.DecideAsync(engine.Ledger, flow, runtime.Parameters, runtime.Mapping.Context, ct).ConfigureAwait(false);

        // A flow limited to the first records of its scope (source.record.maxRecords) reads them on every run, as a run reads them.
        if (ScopeReads.FirstRecords(flow, DeliveryRunPayload.None, null) is { } first)
        {
            var firstOfScope = await ScopeReads.FirstOfScopeAsync(runtime.Source, first.Count, first.Setting, engine.Loggers.CreateLogger("check"), ct).ConfigureAwait(false);
            read = read with { Selection = firstOfScope, Reason = ScopeReadReason.FirstRecords };
        }

        var opened = await runtime.Planner.OpenAsync(flow, runtime.Mapping, runtime.Parameters, read.Selection, gate: false, ct: ct).ConfigureAwait(false);
        var header = opened.Source;
        var selection = read.Selection;
        var scope = read.Scope;
        var watermark = read.Watermark;
        var columns = new JsonObject();
        foreach (var (dataset, names) in header.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Take(MaxDatasetsShown + 1))
        {
            columns[dataset] = new JsonArray(names.Order(StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
        }

        return new JsonObject
        {
            ["scope"] = scope,
            ["selection"] = selection.Describe(),
            ["why"] = read.Why(flow, runtime.Mapping.Context),
            ["watermark"] = watermark is null ? null : watermark.UpdatedThroughUtc.ToString("O", CultureInfo.InvariantCulture),
            ["window"] = new JsonObject
            {
                ["from"] = header.Window.LowerUtc?.ToString("O", CultureInfo.InvariantCulture),
                ["to"] = header.Window.UpperUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            ["candidates"] = header.EstimatedCandidates,
            ["hasChanges"] = header.HasChanges,
            ["keyColumns"] = new JsonArray(header.KeyColumns
                .Select(k => (JsonNode)new JsonObject { ["name"] = k.Name, ["type"] = k.SqlType })
                .ToArray()),
            ["systemColumns"] = new JsonObject
            {
                ["updated"] = flow.Source.SystemColumns.Updated,
                ["fileName"] = flow.Source.SystemColumns.FileName,
                ["rowNumber"] = flow.Source.SystemColumns.RowNumber,
                ["deleted"] = flow.Source.SystemColumns.Deleted,
                ["inserted"] = flow.Source.SystemColumns.Inserted,
            },
            ["columns"] = columns,

            // What the preflight says of the mapping against the tables without stopping a run, as a run logs it.
            ["issues"] = new JsonArray(opened.Issues
                .Where(i => i.Severity == IssueSeverity.Warning)
                .Select(i => (JsonNode)JsonValue.Create(i.ToString()))
                .ToArray()),
        };
    }

    private static void WriteRead(CliVerbContext context, JsonObject read)
    {
        context.Out.WriteLine($"    scope       {read["scope"]?.GetValue<string>()} ({read["selection"]?.GetValue<string>()})");
        context.Out.WriteLine($"    reads       {read["why"]?.GetValue<string>()}");
        var from = read["window"]?["from"]?.GetValue<string>();
        var to = read["window"]?["to"]?.GetValue<string>();
        context.Out.WriteLine($"    window      {(from is null ? "everything" : "after " + from)} through {to}");
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    candidates  {read["candidates"]?.GetValue<long>() ?? 0} record(s) in the window; changes: {read["hasChanges"]?.GetValue<bool>() ?? false}"));
        if (read["keyColumns"] is JsonArray keys)
        {
            context.Out.WriteLine($"    key         {string.Join(", ", keys.Select(k => $"{k?["name"]?.GetValue<string>()} {k?["type"]?.GetValue<string>()}"))}");
        }

        if (read["columns"] is JsonObject columns)
        {
            foreach (var (dataset, names) in columns)
            {
                var count = names is JsonArray array ? array.Count : 0;
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    table       {dataset}: {count} column(s)"));
            }
        }

        if (read["issues"] is JsonArray issues)
        {
            foreach (var issue in issues)
            {
                context.Out.WriteLine($"    issue       {issue?.GetValue<string>()}");
            }
        }
    }

    public static async Task<int> CacheAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } target)
        {
            return context.UsageError("name the partition or the cache document.");
        }

        var store = engine.Cache
            ?? throw new FlowValidationException("Caches live in the module's database. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.");

        switch (verb)
        {
            case "list":
            {
                // A partition is often named ${env:...}, by a cache flow or on the command line; the cache is keyed by
                // what that resolves to, as a capture and an import key it, so the partition is resolved first. A cache flow
                // that works in partitions is listed for the one --partition names, or for every one it serves.
                var scopes = new List<string>();
                if (File.Exists(target))
                {
                    var cache = engine.Documents.LoadCache(target);
                    var requested = CliPartitions.Requested(context) ?? (cache.Partitioned ? PartitionNames.Every : null);
                    foreach (var bound in await CliPartitions.ForRunAsync(context, cache, requested, ct).ConfigureAwait(false))
                    {
                        scopes.Add(await ResolvedScopeAsync(engine, bound.Scope, target, ct).ConfigureAwait(false));
                    }
                }
                else
                {
                    scopes.Add(await ResolvedScopeAsync(engine, target, "sqlflow cache list", ct).ConfigureAwait(false));
                }

                var listed = new List<(string Scope, IReadOnlyList<CacheVersionInfo> Versions, int RetentionDays)>();
                foreach (var scope in scopes)
                {
                    listed.Add((
                        scope,
                        await store.ListVersionsAsync(scope, ct).ConfigureAwait(false),
                        (await store.DeclarationAsync(scope, ct).ConfigureAwait(false)).RetentionDays));
                }

                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray(
                        listed.SelectMany(l => l.Versions.Select(v => (JsonNode)Describe(v, l.RetentionDays))).ToArray())));
                    return 0;
                }

                for (var i = 0; i < listed.Count; i++)
                {
                    if (i > 0)
                    {
                        context.Out.WriteLine();
                    }

                    WriteVersions(context, listed[i].Scope, listed[i].Versions, listed[i].RetentionDays);
                }

                return 0;
            }

            case "import":
            {
                if (context.Arguments.GetOption("--from-dir") is not { } directory)
                {
                    return context.UsageError("name the directory the type files are in with --from-dir.");
                }

                // A cache flow that works in partitions is imported into the one --partition names, or the one a refresh
                // would take: files hold one partition's records, whose ids name it.
                var bound = await CliPartitions.ForRunAsync(context, engine.Documents.LoadCache(target), CliPartitions.Requested(context), ct).ConfigureAwait(false);
                if (bound.Count > 1)
                {
                    return context.UsageError(
                        $"the cache flow builds a cache for {string.Join(", ", bound.Select(b => b.Partition))}; name the one the files belong to with --partition.");
                }

                var cache = bound[0];
                var full = Path.GetFullPath(directory);
                // Keyed by the partition the document actually names, resolved, so an offline import lands in the same
                // cache a capture writes and a render reads.
                var importScope = await ResolvedScopeAsync(engine, cache.Scope, cache.SourcePath ?? cache.Name, ct).ConfigureAwait(false);
                var builder = new SnapshotBuilder(store, importScope, cache.Name, engine.Time, engine.Loggers.CreateLogger<SnapshotBuilder>(), cache.RetentionDays);
                var write = await builder.ImportDirectoryAsync(
                    full, cache.Types, new CacheCapture(null, RunActors.LocalAccount(), $"files under {full}"), ct).ConfigureAwait(false);
                var records = write.Snapshot.Types.Sum(t => t.Items.Count);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
                    {
                        ["partition"] = importScope,
                        ["flow"] = cache.Name,
                        ["version"] = write.Snapshot.Version,
                        ["written"] = write.Written,
                        ["types"] = write.Snapshot.Types.Count,
                        ["records"] = records,
                        ["typesMoved"] = new JsonArray(write.Changes.Moved.Select(name => (JsonNode)JsonValue.Create(name)).ToArray()),
                        ["typesRemoved"] = new JsonArray(write.Changes.Removed.Select(name => (JsonNode)JsonValue.Create(name)).ToArray()),
                        ["retention"] = write.Retention is { } retention ? DescribeRetention(retention) : null,
                    }));
                    return 0;
                }

                context.Out.WriteLine(write.Written
                    ? $"cache of partition {importScope}: version {write.Snapshot.Version} written from cache flow {cache.Name}, holding {write.Snapshot.Types.Count} type(s) and {records} record(s), now current; {(write.Changes.Moved.Count == 0 ? "no type changed" : "types moved: " + string.Join(", ", write.Changes.Moved))}"
                    : $"cache of partition {importScope}: the files add nothing version {write.Snapshot.Version} does not already hold, so nothing was written");
                if (write.Retention is { } applied)
                {
                    context.Out.WriteLine(RetentionLine(importScope, applied));
                }

                return 0;
            }

            case "prune":
                return await CachePruneAsync(ModuleCommand.Of(context), target).ConfigureAwait(false);

            default:
                return context.UsageError($"'{verb}' is not a cache subcommand.");
        }
    }

    /// <summary>
    /// <c>sqlflow cache prune</c>: an operator's purge of a partition cache's history, through the retention every refresh
    /// applies, keeping <c>--keep-days</c> (the partition's own retention when left out). The pruned versions record the
    /// account the command runs as. <c>--dry-run</c> says what it would prune and changes nothing.
    /// </summary>
    internal static async Task<int> CachePruneAsync(ModuleCommand command, string target)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var ct = command.CancellationToken;
        var engine = command.Services.GetRequiredService<EngineContext>();
        var store = engine.Cache
            ?? throw new FlowValidationException("Caches live in the module's database. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.");

        // The option given without a value it takes (none, or a token that reads as an option, such as -1) is refused as one
        // given a value out of range: a purge never falls back to other days than the ones asked for.
        int? keepDays = null;
        if (command.Arguments.HasFlag("--keep-days"))
        {
            var text = command.Arguments.GetOption("--keep-days");
            if (text is null || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed > CacheRetention.MaxDays)
            {
                var given = text ?? FollowingToken(command.Arguments, "--keep-days");
                return command.UsageError(
                    $"--keep-days is a whole number of days from 0 to {CacheRetention.MaxDays}{(given is null ? ", and none was given" : $", not '{given}'")}.");
            }

            keepDays = parsed;
        }

        var scope = await ResolvedScopeAsync(engine, target, "sqlflow cache prune", ct).ConfigureAwait(false);
        if (await store.CurrentVersionAsync(scope, ct).ConfigureAwait(false) is null)
        {
            throw new FlowValidationException($"The cache of partition '{scope}' holds no version, so it has no history to prune.");
        }

        var days = keepDays ?? (await store.DeclarationAsync(scope, ct).ConfigureAwait(false)).RetentionDays;
        var dryRun = command.Arguments.HasFlag("--dry-run");
        var outcome = await store.ApplyRetentionAsync(
            CacheRetentionRequest.Purge(scope, days, engine.Time.GetUtcNow(), RunActors.LocalAccount(), dryRun), ct).ConfigureAwait(false);
        if (command.Json)
        {
            var json = DescribeRetention(outcome);
            json["partition"] = scope;
            json["dryRun"] = outcome.DryRun;
            command.Out.WriteLine(CanonicalJson.Pretty(json));
            return 0;
        }

        command.Out.WriteLine($"cache of partition {scope}: {outcome.DescribePurge()}");
        foreach (var version in outcome.Pruned)
        {
            command.Out.WriteLine($"  {(outcome.DryRun ? "would prune" : "pruned")}  {version}");
        }

        return 0;
    }

    /// <summary>The token right after <paramref name="option"/> on the command line, or null when it is the last one.</summary>
    private static string? FollowingToken(CliArguments arguments, string option)
    {
        var all = arguments.All;
        for (var i = 0; i < all.Count - 1; i++)
        {
            if (string.Equals(all[i], option, StringComparison.Ordinal))
            {
                return all[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// A partition as the cache is keyed by it: <paramref name="declared"/>, a partition id or a reference, resolved the way
    /// a capture and a render resolve it.
    /// </summary>
    private static async Task<string> ResolvedScopeAsync(EngineContext engine, string declared, string where, CancellationToken ct)
        => CacheScope.Normalize(await engine.Secrets.ResolveAsync(CacheScope.Normalize(declared, where), ct).ConfigureAwait(false), where);

    /// <summary>What a retention did, as the import's output says it.</summary>
    private static string RetentionLine(string scope, CacheRetentionOutcome retention)
    {
        if (retention.Failure is { } failure)
        {
            return $"retention of partition {scope}: not applied ({failure}); the next refresh or import applies it";
        }

        if (retention.Deferred is { } waiting)
        {
            return $"retention of partition {scope}: {retention.RetentionDays} day(s), nothing pruned yet: {waiting}";
        }

        return retention.Pruned.Count == 0 && retention.RowsRemoved == 0
            ? $"retention of partition {scope}: {retention.RetentionDays} day(s), nothing to prune; {retention.Kept} version(s) keep their records"
            : $"retention of partition {scope}: {retention.RetentionDays} day(s), pruned {retention.Pruned.Count} version(s) and {retention.RowsRemoved} stored row(s); {retention.Kept} version(s) keep their records";
    }

    private static JsonObject DescribeRetention(CacheRetentionOutcome retention) => new()
    {
        ["retentionDays"] = retention.RetentionDays,
        ["kept"] = retention.Kept,
        ["pruned"] = new JsonArray(retention.Pruned.Select(version => (JsonNode)JsonValue.Create(version)).ToArray()),
        ["rowsRemoved"] = retention.RowsRemoved,
        ["failure"] = retention.Failure,
        ["deferred"] = retention.Deferred,
    };

    /// <summary>
    /// The versions of one partition's cache, newest first, each marked when its records are pruned, the partition's
    /// retention, and the system properties its current version holds.
    /// </summary>
    private static void WriteVersions(CliVerbContext context, string scope, IReadOnlyList<CacheVersionInfo> versions, int retentionDays)
    {
        if (versions.Count == 0)
        {
            context.Out.WriteLine($"the cache of partition {scope} holds no version yet; run a cache flow of the partition with the refresh operation to capture one");
            return;
        }

        context.Out.WriteLine(
            $"partition {scope}: the records of a replaced version are kept {retentionDays} day(s) after it was replaced, then pruned; a pruned version is listed with what it was");
        foreach (var v in versions)
        {
            var run = v.RunId is { } runId ? $" in run {runId:D}" : string.Empty;
            var state = v.Current ? "current" : v.PrunedUtc is not null ? "pruned " : "       ";
            var pruned = v.PrunedUtc is { } at
                ? $"; records pruned at {at.ToString("u", CultureInfo.InvariantCulture)}{(v.PrunedBy is { } by ? $" by {by}" : string.Empty)}"
                : string.Empty;
            context.Out.WriteLine(
                $"{v.Version}  {state}  {v.Items} record(s) in {v.Types.Count} type(s), written by cache flow {v.FlowName} at {v.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} for {v.CapturedBy}{run}{pruned}");

            // Which of its types the version moved, by each type's content hash; a version written before types were hashed
            // recorded none, and says nothing here.
            if (v.Types.Any(t => t.Change is not null))
            {
                var moved = v.Types
                    .Where(t => t.Change is CacheTypeChange.Added or CacheTypeChange.Changed)
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .Select(t => $"{t.Name} ({CacheTypeChanges.Text(t.Change!.Value)})")
                    .ToList();
                context.Out.WriteLine(moved.Count == 0
                    ? "  no type changed"
                    : $"  types moved: {string.Join(", ", moved)}; {v.Types.Count - moved.Count} unchanged");
            }
        }

        if (versions.FirstOrDefault(v => v.Current) is not { } current)
        {
            return;
        }

        context.Out.WriteLine();
        if (current.SystemProperties.Count == 0)
        {
            context.Out.WriteLine($"system properties: none read yet; refresh a cache flow of partition {scope} to read them");
            return;
        }

        context.Out.WriteLine($"system properties of partition {scope}, as version {current.Version} holds them (settings of the platform, not cached records):");
        foreach (var property in current.SystemProperties)
        {
            var source = property.Source is null ? string.Empty : $"  from {property.Source}";
            var detail = property.Detail is null ? string.Empty : $"  ({property.Detail})";
            context.Out.WriteLine($"  {property.Service,-8}  {property.Name}  {property.State.ToString().ToLowerInvariant()}{source}{detail}");
        }
    }

    private static JsonObject Describe(CacheVersionInfo v, int retentionDays) => new()
    {
        ["partition"] = v.Scope,
        ["flow"] = v.FlowName,
        ["version"] = v.Version,
        ["sequence"] = v.Sequence,
        ["current"] = v.Current,
        ["prunedUtc"] = v.PrunedUtc?.ToString("O", CultureInfo.InvariantCulture),
        ["prunedBy"] = v.PrunedBy,
        ["retentionDays"] = retentionDays,
        ["capturedUtc"] = v.CapturedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["capturedBy"] = v.CapturedBy,
        ["runId"] = v.RunId?.ToString("D"),
        ["origin"] = v.Origin,
        ["previousVersion"] = v.PreviousVersion,
        ["records"] = v.Items,
        ["types"] = new JsonArray(v.Types.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["entityType"] = t.EntityType,
            ["records"] = t.Items,
            ["hash"] = t.Hash,
            ["change"] = t.Change is { } change ? CacheTypeChanges.Text(change) : null,
            ["since"] = t.Since,
        }).ToArray()),
        ["systemProperties"] = new JsonArray(v.SystemProperties.Select(p => (JsonNode)new JsonObject
        {
            ["service"] = p.Service,
            ["name"] = p.Name,
            ["state"] = p.State.ToString(),
            ["source"] = p.Source,
            ["detail"] = p.Detail,
        }).ToArray()),
    };

    public static Task<int> TemplateAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TemplateAsync(ModuleCommand.Of(context));
    }

    /// <summary>
    /// <c>sqlflow template</c> over the command it runs for, whether the command line or a suite runs it. A template is a file
    /// of a repository's template folder (osdu/docs/reference/concepts/templates.md), so every subcommand reads or writes
    /// files and none needs a database: <c>folders</c> reads a working copy's template folders, and <c>capture</c> and
    /// <c>import</c> write a template file into one, which the repository's next sync records.
    /// </summary>
    internal static async Task<int> TemplateAsync(ModuleCommand context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;

        switch (verb)
        {
            case "folders":
                return Folders(context, ct);

            case "capture":
            {
                // The canonical OSDU schemas are the Open Group's data definitions, not whatever one platform happens to host.
                if (context.Arguments.Positional(2) is not null)
                {
                    return context.UsageError("'sqlflow template capture' takes no flow: it writes the kind's schema from the OSDU data definitions.");
                }

                if (context.Arguments.GetOption("--kind") is not { } kind)
                {
                    return context.UsageError("name the kind to capture with --kind.");
                }

                if (context.Arguments.GetOption("--out") is not { } outPath)
                {
                    return context.UsageError(OutRequired);
                }

                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var file = await DataDefinitions(http, engine.Time).FetchAsync(context.Arguments.GetOption("--release"), kind, ct).ConfigureAwait(false);
                return Report(context, file.Schema, await WriteTemplateFileAsync(file.Schema, outPath, ct).ConfigureAwait(false));
            }

            case "import":
            {
                if (context.Arguments.GetOption("--kind") is not { } kind)
                {
                    return context.UsageError("name the kind the schema holds with --kind.");
                }

                if (context.Arguments.GetOption("--out") is not { } outPath)
                {
                    return context.UsageError(OutRequired);
                }

                if (context.Arguments.GetOption("--from-dir") is { } directory)
                {
                    var schema = await TemplateSources.FromDirectoryAsync(directory, kind, engine.Time, ct).ConfigureAwait(false);
                    return Report(context, schema, await WriteTemplateFileAsync(schema, outPath, ct).ConfigureAwait(false));
                }

                // A bundled file is written as it is. A file as the data definitions publish it is bundled with the shared
                // schemas it refers to from --release (the newest when it is not given).
                if (context.Arguments.Positional(2) is not { } path)
                {
                    return context.UsageError("name the schema file to import, or the data definitions directory with --from-dir.");
                }

                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var imported = await DataDefinitions(http, engine.Time)
                    .ImportAsync(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), kind, context.Arguments.GetOption("--release"), path, ct)
                    .ConfigureAwait(false);

                // A schema of one's own goes into a template folder as the file --out writes: bundled, and naming its kind.
                return Report(context, imported.Schema, await WriteTemplateFileAsync(imported.Schema, outPath, ct).ConfigureAwait(false));
            }

            default:
                return context.UsageError($"'{verb}' is not a template subcommand: folders, capture or import.");
        }
    }

    /// <summary>What a capture or an import is told without <c>--out</c>: a template is a file, and the database keeps none.</summary>
    private const string OutRequired =
        "name the template file to write with --out, in a template folder of the repository (<folder>/templates/<file>.json): a template is a file the repository holds, which its next sync records, and the database keeps none.";

    /// <summary>The OSDU data definitions over <paramref name="http"/>, kept in the default local copy.</summary>
    private static OsduDataDefinitions DataDefinitions(HttpClient http, TimeProvider time)
        => new(
            () => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, OsduDataDefinitions.DefaultCacheDirectory,
            OsduDataDefinitions.DefaultFreshness, OsduDataDefinitions.DefaultDownloadTimeout, time);

    /// <summary>
    /// Writes a template as a file (<see cref="TemplateSources.FileText"/>) to <paramref name="outPath"/>, creating the
    /// folders it needs, and answers the file's full path. A path that cannot be written is refused with the reason, as
    /// every other refusal of the verb is.
    /// </summary>
    private static async Task<string> WriteTemplateFileAsync(SchemaSnapshot schema, string outPath, CancellationToken ct)
    {
        var reference = new TemplateReference(schema.Kind, schema.Version);
        try
        {
            var path = Path.GetFullPath(outPath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, TemplateSources.FileText(schema), ct).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DeliveryException($"The bundled schema of template {reference} could not be written to '{outPath}': {ex.Message}", ex);
        }
    }

    /// <summary>What a capture or an import wrote: the template and the file, which the repository's next sync records.</summary>
    private static int Report(ModuleCommand context, SchemaSnapshot schema, string path)
    {
        var reference = new TemplateReference(schema.Kind, schema.Version);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["kind"] = reference.Kind,
                ["version"] = reference.Version,
                ["file"] = path,
                ["outcome"] = "written",
            }));
            return 0;
        }

        context.Out.WriteLine($"wrote template {reference} to {path}");
        if (!InTemplateFolder(path))
        {
            // Written outside a template folder, the file is read by no document until it is moved into one.
            context.Error.WriteLine($"{path} is not in a folder named {TemplateFolders.FolderName}, so no document reads it there; move it into a template folder on the path of the documents that fill it.");
        }

        return 0;
    }

    /// <summary>Whether a folder on <paramref name="path"/>'s path is named <c>templates</c>, exactly, as a template folder is.</summary>
    private static bool InTemplateFolder(string path)
    {
        for (var directory = Path.GetDirectoryName(path); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (string.Equals(Path.GetFileName(directory), TemplateFolders.FolderName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>sqlflow template folders [&lt;dir&gt;] [--for &lt;document&gt;]</c>: a repository's template folders as a run reads them
    /// (osdu/docs/reference/concepts/templates.md, Template folders), read from disk with no database. Without
    /// <c>--for</c>, every template folder under the directory (the repository's top above the current folder by default),
    /// each file with the template it holds or why it holds none. With <c>--for</c>, the folders the document reads,
    /// nearest first, and the version each kind they hold settles to for a document that names the kind alone.
    /// </summary>
    private static int Folders(ModuleCommand context, CancellationToken ct)
    {
        var directory = context.Arguments.Positional(2);
        if (context.Arguments.GetOption("--for") is { } document)
        {
            var full = Path.GetFullPath(document);
            if (!File.Exists(full))
            {
                return context.UsageError($"--for names '{document}', which is not a file; name the mapping or flow whose template folders to read.");
            }

            var chain = TemplateFolders.ChainFor(full, directory is null ? null : Path.GetFullPath(directory));
            var kinds = chain.Templates.Select(f => f.Kind!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
                .Select(kind => (Kind: kind, Pick: chain.Pick(kind, null)))
                .ToList();
            if (context.Json)
            {
                context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
                {
                    ["document"] = chain.Document,
                    ["looked"] = new JsonArray(chain.Looked.Select(l => (JsonNode)l).ToArray()),
                    ["folders"] = new JsonArray(chain.Folders.Select(f => (JsonNode)FolderJson(f)).ToArray()),
                    ["kinds"] = new JsonArray(kinds.Select(k => (JsonNode)new JsonObject
                    {
                        ["kind"] = k.Kind,
                        ["version"] = k.Pick.File?.Version,
                        ["file"] = k.Pick.File?.Shown,
                        ["problem"] = k.Pick.Problem,
                    }).ToArray()),
                }));
                return 0;
            }

            if (chain.Folders.Count == 0)
            {
                context.Out.WriteLine(
                    $"{chain.Document} reads no template folder: a document reads the folder named templates beside it and those above it, and there is none ({TemplateChain.Listed(chain.Looked)}).");
                return 0;
            }

            context.Out.WriteLine($"{chain.Document} reads {chain.Folders.Count} template folder(s), nearest first:");
            foreach (var folder in chain.Folders)
            {
                WriteFolder(context, folder);
            }

            context.Out.WriteLine("A kind named without a version settles to:");
            foreach (var (kind, pick) in kinds)
            {
                context.Out.WriteLine(pick.Found
                    ? $"  {kind}  {pick.File!.Version}  {pick.File.Shown}"
                    : $"  {kind}  not settled: {pick.Problem}");
            }

            return 0;
        }

        var root = directory is null
            ? TemplateFolders.RepositoryTop(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory()
            : Path.GetFullPath(directory);
        var scan = TemplateFolders.Scan(root, ct);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["root"] = root,
                ["folders"] = new JsonArray(scan.Folders.Select(f => (JsonNode)FolderJson(f)).ToArray()),
                ["warnings"] = new JsonArray(scan.Warnings.Select(w => (JsonNode)w).ToArray()),
            }));
            return 0;
        }

        if (scan.Folders.Count == 0)
        {
            context.Out.WriteLine($"no template folder under {root}");
        }

        foreach (var folder in scan.Folders)
        {
            WriteFolder(context, folder);
        }

        foreach (var warning in scan.Warnings)
        {
            context.Error.WriteLine($"WARN   {warning}");
        }

        return 0;
    }

    private static void WriteFolder(ModuleCommand context, TemplateFolder folder)
    {
        context.Out.WriteLine(folder.Shown.Length == 0 ? "." : folder.Shown);
        foreach (var file in folder.Files)
        {
            context.Out.WriteLine(file.IsTemplate
                ? $"  {file.Shown}  {file.Kind}  {file.Version}{Linked(file)}"
                : $"  {file.Shown}  holds no template: {file.Problem}");
        }
    }

    /// <summary>
    /// The kind a template file is linked to as its parent (osdu/docs/reference/concepts/templates.md, Linked templates), or
    /// why its link cannot be read, after its version; nothing for a template linked to none.
    /// </summary>
    private static string Linked(TemplateFolderFile file)
        => file.Parent is { } parent ? $"  linked to {parent}"
            : file.ParentProblem is { } problem ? $"  linked to no parent: {problem}"
            : string.Empty;

    private static JsonObject FolderJson(TemplateFolder folder) => new()
    {
        ["path"] = folder.Shown,
        ["files"] = new JsonArray(folder.Files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Shown,
            ["kind"] = f.Kind,
            ["version"] = f.Version,
            ["problem"] = f.Problem,
            ["parent"] = f.Parent,
            ["parentProblem"] = f.ParentProblem,
        }).ToArray()),
    };

    private static JsonObject Describe(TemplateVariable v) => new()
    {
        ["path"] = v.Path.Text,
        ["shape"] = v.Shape.ToString(),
        ["type"] = v.Type,
        ["itemType"] = v.ItemType,
        ["required"] = v.Required,
        ["role"] = v.Role.ToString(),
        ["relationships"] = new JsonArray(v.Relationships.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
        ["unitContext"] = v.UnitContext,
        ["description"] = v.Description,
    };

    private static string Shape(TemplateVariable v) => v.Shape switch
    {
        TemplateVariableShape.ValueList => $"list of {v.ItemType}",
        TemplateVariableShape.Group => "object",
        TemplateVariableShape.GroupList => "list of objects",
        TemplateVariableShape.Whole => $"whole {v.Type}",
        _ => v.Type,
    };

    /// <summary>How the flow's searches are written, and the system properties of the partition that decide it.</summary>
    private static string SearchLine(RenderContext context)
    {
        var rule = context.KeywordLower
            ? "exact, then regardless of case where one record answers"
            : "exact only";
        var properties = string.Join(", ", context.SystemProperties.Select(p => $"{p.Service} {p.Name} {p.State.ToString().ToLowerInvariant()}"));
        return $"    searches    {rule} ({properties})";
    }
}
