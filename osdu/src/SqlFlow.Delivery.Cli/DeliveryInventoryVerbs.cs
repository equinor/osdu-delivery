using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>inventory</c> verb: the inventories of inventory flows where an operator already is, at a terminal or in a script,
/// without a control plane to reach (osdu/docs/reference/cli/inventory.md). <c>list</c> shows a partition's inventories with
/// what their last reconcile raised, <c>show</c> one with its counts by finding, owners and last runs, <c>records</c> pages
/// through its ids of one finding, <c>lookup</c> says what every inventory of a partition holds of one OSDU id, <c>runs</c>
/// lists its builds and reconciles, and <c>export</c> writes its ids as CSV, by the same code as the API's download;
/// <c>removals</c> lists the removals an operator asked of it, and <c>removal</c> pages through what one did to each id.
/// Building is a run like any other: <c>sqlflow run &lt;flow.yaml&gt; --payload '{"inventories":["name"]}'</c>, and so is a
/// removal, with <c>--operation remove</c> and the payload the inventory's page sends (osdu/docs/reference/flow/inventory-removals.md,
/// What a removal does).
/// </summary>
internal static class DeliveryInventoryVerbs
{
    /// <summary>Ids listed when the command line asks for no count.</summary>
    private const int DefaultRecords = 50;

    /// <summary>The most ids one page lists.</summary>
    private const int MaxRecords = 1000;

    /// <summary>Runs listed when the command line asks for no count.</summary>
    private const int DefaultRuns = 20;

    /// <summary>The most runs one listing shows.</summary>
    private const int MaxRuns = 200;

    public static async Task<int> InventoryAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (verb is not ("list" or "show" or "records" or "lookup" or "runs" or "export" or "removals" or "removal"))
        {
            return context.UsageError("say what to show: list, show, records, lookup, runs, export, removals or removal.");
        }

        var engine = context.Services.GetRequiredService<EngineContext>();
        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Inventories live in the module's database. Run 'sqlflow inventory' with --db <conn-ref>, or set the catalog variable.");
        if (verb == "list")
        {
            return await ListAsync(context, ledger, ct).ConfigureAwait(false);
        }

        if (context.Arguments.Positional(2) is not { } partitionText)
        {
            return context.UsageError($"name the partition, then the {(verb == "lookup" ? "OSDU id to look up" : "inventory's number")}: sqlflow inventory {verb} <partition> {(verb == "lookup" ? "<osdu-id>" : "<id>")}.");
        }

        var partition = Partition(partitionText);
        if (verb == "lookup")
        {
            return context.Arguments.Positional(3) is { } id && !string.IsNullOrWhiteSpace(id)
                ? await LookupAsync(context, ledger, partition, id.Trim(), ct).ConfigureAwait(false)
                : context.UsageError("name the OSDU id to look up: sqlflow inventory lookup <partition> <osdu-id>.");
        }

        if (verb == "removal")
        {
            return context.Arguments.Positional(3) is { } removalText
                && long.TryParse(removalText, NumberStyles.None, CultureInfo.InvariantCulture, out var removalId) && removalId > 0
                ? await RemovalAsync(context, ledger, partition, removalId, ct).ConfigureAwait(false)
                : context.UsageError("name the removal by its number, as 'sqlflow inventory removals' shows it: sqlflow inventory removal <partition> <removal>.");
        }

        if (context.Arguments.Positional(3) is not { } idText)
        {
            return context.UsageError($"name the inventory by its number, as 'sqlflow inventory list' shows it: sqlflow inventory {verb} <partition> <id>.");
        }

        if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var inventoryId) || inventoryId < 1)
        {
            return context.UsageError($"'{idText}' is not an inventory's number; 'sqlflow inventory list' shows them.");
        }

        var inventory = await ledger.GetInventoryAsync(partition, inventoryId, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException($"No inventory {inventoryId} in partition '{partition}'; 'sqlflow inventory list --partition {partition}' shows its inventories.");
        return verb switch
        {
            "show" => await ShowAsync(context, ledger, inventory, ct).ConfigureAwait(false),
            "records" => await RecordsAsync(context, ledger, inventory, ct).ConfigureAwait(false),
            "runs" => await RunsAsync(context, ledger, inventory, ct).ConfigureAwait(false),
            "removals" => await RemovalsAsync(context, ledger, inventory, ct).ConfigureAwait(false),
            _ => await ExportAsync(context, ledger, inventory, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The inventories of the partition --partition names (of every partition without it), with what their last reconcile raised.</summary>
    private static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, CancellationToken ct)
    {
        var partition = context.Arguments.GetOption("--partition") is { } named ? Partition(named) : null;
        var inventories = await ledger.ListInventoriesAsync(partition, ct).ConfigureAwait(false);
        var listed = new List<(InventoryState Inventory, InventoryRecentRuns Runs, IReadOnlyDictionary<string, long>? Findings)>(inventories.Count);
        foreach (var inventory in inventories)
        {
            var runs = await InventoryReport.RecentRunsAsync(ledger, inventory, ct).ConfigureAwait(false);
            listed.Add((inventory, runs, InventoryReport.Findings(runs.LastReconcile?.FindingsJson)));
        }

        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["partition"] = partition,
                ["inventories"] = new JsonArray(listed.Select(l =>
                {
                    var described = Described(l.Inventory);
                    described["reconciled"] = l.Findings is null ? null : Counts(l.Findings, zeros: false);
                    described["raised"] = l.Findings is null ? null : new InventoryCounts(l.Findings).Raised;
                    described["latest"] = l.Runs.Latest is null ? null : Described(l.Runs.Latest);
                    return (JsonNode)described;
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{listed.Count} inventory(ies) in {(partition is null ? "every partition" : partition)}"));
        if (listed.Count == 0)
        {
            context.Out.WriteLine("  none yet. An inventory flow's build registers its inventories: sqlflow run <flow.yaml>");
        }

        foreach (var (inventory, runs, findings) in listed)
        {
            var reconciled = findings is null
                ? inventory.LastBuiltUtc is null ? "not built yet" : "not reconciled yet"
                : string.Create(CultureInfo.InvariantCulture, $"{new InventoryCounts(findings).Raised} raised, reconciled {Stamp(inventory.LastReconciledUtc!.Value)}");
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {inventory.InventoryId,6}  {inventory.Partition}  {inventory.FlowName} / {inventory.Name}  {inventory.Kind}  {reconciled}"));
            if (runs.Latest is { } latest && latest.InventoryRunId != runs.LastReconcile?.InventoryRunId)
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"          newest {latest.Operation} {latest.InventoryRunId} {latest.Status} at {Stamp(latest.StartedUtc)}{(latest.Error is { Length: > 0 } error ? ": " + error : string.Empty)}"));
            }
        }

        return 0;
    }

    /// <summary>One inventory: what it reads, its last build and reconcile, the owners it used, and its ids by finding as its rows hold them now.</summary>
    private static async Task<int> ShowAsync(CliVerbContext context, ILedger ledger, InventoryState inventory, CancellationToken ct)
    {
        var runs = await InventoryReport.RecentRunsAsync(ledger, inventory, ct).ConfigureAwait(false);
        var counts = await ledger.InventoryCountsAsync(inventory.Partition, inventory.InventoryId, ct).ConfigureAwait(false);
        var owners = InventoryReport.Owners(inventory.OwnersJson, inventory.OwnersSource);
        if (context.Json)
        {
            var described = Described(inventory);
            described["counts"] = Counts(counts.ByFinding, zeros: true);
            described["ids"] = counts.ByFinding.Values.Sum();
            described["raised"] = counts.Raised;
            described["owners"] = owners is null ? null : Described(owners);
            described["latest"] = runs.Latest is null ? null : Described(runs.Latest);
            described["lastBuild"] = runs.LastBuild is null ? null : Described(runs.LastBuild);
            described["lastReconcile"] = runs.LastReconcile is null ? null : Described(runs.LastReconcile);
            context.Out.WriteLine(CanonicalJson.Pretty(described));
            return 0;
        }

        var output = context.Out;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{inventory.Name}: inventory {inventory.InventoryId} of {inventory.FlowName} in {inventory.Partition}"));
        output.WriteLine($"  reads {inventory.Kind} through {inventory.ReadMode}, {(inventory.Versions == "all" ? "every version" : "the latest version")}{(inventory.Query is { Length: > 0 } query ? $", where {query}" : string.Empty)}");
        output.WriteLine("  last build      " + RunLine(runs.LastBuild, inventory.LastBuiltUtc));
        output.WriteLine("  last reconcile  " + RunLine(runs.LastReconcile, inventory.LastReconciledUtc));
        if (runs.Latest is { } latest && latest.InventoryRunId != runs.LastBuild?.InventoryRunId && latest.InventoryRunId != runs.LastReconcile?.InventoryRunId)
        {
            output.WriteLine("  newest run      " + RunLine(latest, latest.CompletedUtc));
        }

        output.WriteLine("  owners          " + OwnersLine(owners));
        var total = counts.ByFinding.Values.Sum();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {total} id(s), {counts.Raised} raised"));
        foreach (var count in InventoryReport.Counted(counts.ByFinding).Where(c => c.Count > 0))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {count.Finding,-13} {count.Count,10}{(count.Raised ? "  raised" : string.Empty)}"));
        }

        if (InventoryFindings.Raised.FirstOrDefault(f => counts.Of(f) > 0) is { } first)
        {
            output.WriteLine($"  See them with: sqlflow inventory records {inventory.Partition} {inventory.InventoryId.ToString(CultureInfo.InvariantCulture)} --finding {first}");
        }

        return 0;
    }

    /// <summary>
    /// A page of the inventory's ids, of the finding --finding names or every one, in the order the inventory took them in, after
    /// the id --after names; the line under the page names the --after of the next.
    /// </summary>
    private static async Task<int> RecordsAsync(CliVerbContext context, ILedger ledger, InventoryState inventory, CancellationToken ct)
    {
        string? finding = null;
        if (context.Arguments.GetOption("--finding") is { } named)
        {
            finding = named.Trim().ToLowerInvariant();
            if (!InventoryFindings.IsKnown(finding))
            {
                throw new FlowValidationException($"--finding '{named}' is not a finding; an inventory finds {string.Join(", ", InventoryFindings.All)}.");
            }
        }

        long? after = null;
        if (context.Arguments.GetOption("--after") is { } afterText)
        {
            after = long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                ? from
                : throw new FlowValidationException($"--after '{afterText}' is not an id's number: the one the page before ends with.");
        }

        var limit = Count(context.Arguments.GetOption("--limit"), DefaultRecords, MaxRecords, "--limit");

        // One more than the page is read, so the line under it knows whether another page follows.
        var read = await ledger.ListInventoryRecordsAsync(inventory.Partition, inventory.InventoryId, finding, after, limit + 1, ct).ConfigureAwait(false);
        var page = read.Take(limit).ToList();
        long? next = read.Count > limit ? page[^1].InventoryRecordId : null;
        var names = await LedgerNamesAsync(ledger, page, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["partition"] = inventory.Partition,
                ["inventoryId"] = inventory.InventoryId,
                ["inventory"] = inventory.Name,
                ["finding"] = finding,
                ["records"] = new JsonArray(page.Select(r => (JsonNode)Described(r, names)).ToArray()),
                ["next"] = next,
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{inventory.Name} in {inventory.Partition}: {page.Count} id(s){(finding is null ? string.Empty : $" {finding}")}{(after is null ? string.Empty : $" after {after}")}"));
        foreach (var record in page)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {record.InventoryRecordId,10}  {record.Finding,-12}  {record.TargetId}  {(record.Version is { } version ? "v" + version.ToString(CultureInfo.InvariantCulture) : "no version")}{LedgerText(record, names)}"));
            if (record.Detail is { Length: > 0 } detail)
            {
                context.Out.WriteLine("              " + detail);
            }
        }

        if (next is not null)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  more follow: add --after {next}"));
        }

        return 0;
    }

    /// <summary>What every inventory of the partition holds of one OSDU id: the inventories listing it or expecting it, with its finding in each.</summary>
    private static async Task<int> LookupAsync(CliVerbContext context, ILedger ledger, string partition, string id, CancellationToken ct)
    {
        var records = await ledger.LookupInventoryRecordsAsync(partition, id, ct).ConfigureAwait(false);
        var names = await LedgerNamesAsync(ledger, records, ct).ConfigureAwait(false);
        var inventories = new Dictionary<int, InventoryState?>();
        foreach (var inventoryId in records.Select(r => r.InventoryId).Distinct())
        {
            inventories[inventoryId] = await ledger.GetInventoryAsync(partition, inventoryId, ct).ConfigureAwait(false);
        }

        var hits = records.Where(r => inventories.GetValueOrDefault(r.InventoryId) is not null).ToList();
        var removed = await ledger.LookupInventoryRemovalItemsAsync(partition, id, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["partition"] = partition,
                ["id"] = id,
                ["hits"] = new JsonArray(hits.Select(r => (JsonNode)new JsonObject
                {
                    ["inventory"] = Described(inventories[r.InventoryId]!),
                    ["record"] = Described(r, names),
                }).ToArray()),
                ["removals"] = new JsonArray(removed.Select(i => (JsonNode)Described(i)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{id} in {partition}: {hits.Count} inventory(ies)"));
        if (hits.Count == 0)
        {
            context.Out.WriteLine("  no inventory of the partition lists it or expects it.");
        }

        foreach (var record in hits)
        {
            var inventory = inventories[record.InventoryId]!;
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {inventory.FlowName} / {inventory.Name} (inventory {inventory.InventoryId}): {record.Finding}, {(record.Version is { } version ? "v" + version.ToString(CultureInfo.InvariantCulture) : "no version")}{LedgerText(record, names)}"));
            if (record.Detail is { Length: > 0 } detail)
            {
                context.Out.WriteLine("      " + detail);
            }
        }

        foreach (var item in removed)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  removal {item.InventoryRemovalId} at {Stamp(item.RecordedUtc)}: {item.Outcome} ({item.Finding}){(item.Reason is { Length: > 0 } reason ? ": " + reason : string.Empty)}"));
        }

        return 0;
    }

    /// <summary>The inventory's builds and reconciles, the newest first: what each read, changed and raised.</summary>
    private static async Task<int> RunsAsync(CliVerbContext context, ILedger ledger, InventoryState inventory, CancellationToken ct)
    {
        var limit = Count(context.Arguments.GetOption("--limit"), DefaultRuns, MaxRuns, "--limit");
        var runs = await ledger.ListInventoryRunsAsync(inventory.Partition, inventory.InventoryId, limit, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["partition"] = inventory.Partition,
                ["inventoryId"] = inventory.InventoryId,
                ["inventory"] = inventory.Name,
                ["runs"] = new JsonArray(runs.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{inventory.Name} in {inventory.Partition}: {runs.Count} run(s)"));
        if (runs.Count == 0)
        {
            context.Out.WriteLine("  none yet. Build it with: sqlflow run <flow.yaml>");
        }

        foreach (var run in runs)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {run.InventoryRunId,8}  {run.Operation,-9}  {run.Status,-9}  {Stamp(run.StartedUtc)}  {Outcome(run)}  by {run.Actor}"));
            if (run.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("            " + error);
            }
        }

        return 0;
    }

    /// <summary>The removals an operator asked of the inventory, newest first: what each removed, how much, what it came to, who asked.</summary>
    private static async Task<int> RemovalsAsync(CliVerbContext context, ILedger ledger, InventoryState inventory, CancellationToken ct)
    {
        var limit = Count(context.Arguments.GetOption("--limit"), DefaultRuns, MaxRuns, "--limit");
        var removals = await ledger.ListInventoryRemovalsAsync(inventory.Partition, inventory.InventoryId, limit, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["partition"] = inventory.Partition,
                ["inventoryId"] = inventory.InventoryId,
                ["inventory"] = inventory.Name,
                ["removals"] = new JsonArray(removals.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{inventory.Name} in {inventory.Partition}: {removals.Count} removal(s)"));
        if (removals.Count == 0)
        {
            context.Out.WriteLine("  none. The inventory's page removes its gone ids from it, and, for a flow that declares removal, what it found from OSDU.");
        }

        foreach (var removal in removals)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {removal.InventoryRemovalId,8}  {removal.Status,-9}  {Stamp(removal.StartedUtc)}  {removal.Requested} {removal.Finding} {Which(removal)}, {InventoryRemovals.Describe(removal.Scope)}: {new InventoryRemovalTally(removal.Removed, removal.Gone, removal.Skipped, removal.Failed).Describe()}  by {removal.Actor}"));
            if (removal.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("            " + error);
            }
        }

        return 0;
    }

    /// <summary>A page of what one removal did to each id, of the outcome --outcome names (every one without it).</summary>
    private static async Task<int> RemovalAsync(CliVerbContext context, ILedger ledger, string partition, long removalId, CancellationToken ct)
    {
        var removal = await ledger.GetInventoryRemovalAsync(partition, removalId, ct).ConfigureAwait(false)
            ?? throw new FlowValidationException($"No inventory removal {removalId} in partition '{partition}'; 'sqlflow inventory removals <partition> <id>' lists an inventory's removals.");
        string? outcome = null;
        if (context.Arguments.GetOption("--outcome") is { } named)
        {
            outcome = named.Trim().ToLowerInvariant();
            if (!InventoryRemovals.IsOutcome(outcome))
            {
                throw new FlowValidationException($"--outcome '{named}' is not what a removal comes to for an id; it is one of {string.Join(", ", InventoryRemovals.Outcomes)}.");
            }
        }

        long? after = null;
        if (context.Arguments.GetOption("--after") is { } afterText)
        {
            after = long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                ? from
                : throw new FlowValidationException($"--after '{afterText}' is not an item's number: the one the page before ends with.");
        }

        var limit = Count(context.Arguments.GetOption("--limit"), DefaultRecords, MaxRecords, "--limit");
        var read = await ledger.ListInventoryRemovalItemsAsync(partition, removalId, outcome, after, limit + 1, ct).ConfigureAwait(false);
        var page = read.Take(limit).ToList();
        long? next = read.Count > limit ? page[^1].InventoryRemovalItemId : null;
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["partition"] = partition,
                ["removal"] = Described(removal),
                ["outcome"] = outcome,
                ["items"] = new JsonArray(page.Select(i => (JsonNode)Described(i)).ToArray()),
                ["next"] = next,
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"removal {removal.InventoryRemovalId} in {partition} ({removal.Status}): {removal.Requested} {removal.Finding}, {InventoryRemovals.Describe(removal.Scope)}, by {removal.Actor} at {Stamp(removal.StartedUtc)}"));
        if (removal.Scope == InventoryRemovals.InventoryOnly && removal.Removed > 0 && outcome is null or InventoryRemovals.Removed)
        {
            // What a removal from the inventory removes it does not keep, so only the ids it kept are listed.
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {removal.Removed} id(s) removed from the inventory: the removal keeps how many, not which; the ids listed are those it kept."));
        }

        foreach (var item in page)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {item.InventoryRemovalItemId,8}  {item.Outcome,-8}  {item.TargetId}{(item.Version is { } version ? " v" + version.ToString(CultureInfo.InvariantCulture) : string.Empty)}{(item.Reason is { Length: > 0 } reason ? ": " + reason : string.Empty)}"));
        }

        if (next is { } more)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  more: --after {more}"));
        }

        return 0;
    }

    /// <summary>The inventory's ids of the finding --finding names (every id without it) as CSV, to --out or the console.</summary>
    private static async Task<int> ExportAsync(CliVerbContext context, ILedger ledger, InventoryState inventory, CancellationToken ct)
    {
        string? finding = null;
        if (context.Arguments.GetOption("--finding") is { } named)
        {
            finding = named.Trim().ToLowerInvariant();
            if (!InventoryFindings.IsKnown(finding))
            {
                throw new FlowValidationException($"--finding '{named}' is not a finding; an inventory finds {string.Join(", ", InventoryFindings.All)}.");
            }
        }

        if (context.Arguments.GetOption("--out") is not { } outPath)
        {
            await InventoryExport.WriteAsync(ledger, inventory, finding, context.Out, ct).ConfigureAwait(false);
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
                rows = await InventoryExport.WriteAsync(ledger, inventory, finding, file, ct).ConfigureAwait(false);
            }

            File.Move(partial, full, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {rows} id(s){(finding is null ? string.Empty : $" {finding}")} of {inventory.Name} to {full}"));
        return 0;
    }

    /// <summary>The partition a command line names, as a data-partition-id.</summary>
    private static string Partition(string named)
    {
        var partition = named.Trim();
        return CacheScope.IsPartitionId(partition)
            ? partition
            : throw new FlowValidationException($"'{named}' is not a partition: a data-partition-id is letters, digits, underscore, hyphen and dot.");
    }

    /// <summary>The names of the ledgers the records name, read once each.</summary>
    private static async Task<Dictionary<Guid, string>> LedgerNamesAsync(ILedger ledger, IReadOnlyList<InventoryRecordState> records, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var flowId in records.Select(r => r.LedgerFlowId).OfType<Guid>().Distinct())
        {
            if (await ledger.GetLedgerAsync(flowId, ct).ConfigureAwait(false) is { } entry)
            {
                names[flowId] = entry.LedgerName;
            }
        }

        return names;
    }

    /// <summary>What the ledgers hold of an id, in a phrase after its line: the ledger and the record's status and version, and the artifact.</summary>
    private static string LedgerText(InventoryRecordState record, IReadOnlyDictionary<Guid, string> names)
    {
        var parts = new List<string>();
        if (record.LedgerFlowId is { } flowId)
        {
            var ledger = names.TryGetValue(flowId, out var name) ? name : flowId.ToString("D");
            var status = record.LedgerStatus is null ? string.Empty : $" {record.LedgerStatus}";
            var version = record.LedgerVersion is { } v ? " v" + v.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var key = record.DeliveryKey is { } deliveryKey ? $" record {deliveryKey:D}" : string.Empty;
            parts.Add($"ledger {ledger}{key}{status}{version}");
        }

        if (record.ArtifactId is { } artifact)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"artifact {artifact}{(record.ArtifactState is null ? string.Empty : " " + record.ArtifactState)}"));
        }

        return parts.Count == 0 ? string.Empty : "  " + string.Join("; ", parts);
    }

    private static string RunLine(InventoryRunState? run, DateTime? at)
    {
        if (run is null)
        {
            return at is { } when ? $"at {Stamp(when)}" : "none yet";
        }

        var line = string.Create(CultureInfo.InvariantCulture, $"{run.InventoryRunId} {run.Status} {Stamp(run.CompletedUtc ?? run.StartedUtc)} by {run.Actor}: {Outcome(run)}");
        return run.Error is { Length: > 0 } error ? $"{line} ({error})" : line;
    }

    /// <summary>What a run read, changed and raised, in a phrase.</summary>
    private static string Outcome(InventoryRunState run)
    {
        var read = run.Operation == InventoryRunStatus.Build
            ? string.Create(CultureInfo.InvariantCulture, $"listed {run.Listed} ({run.Added} new, {run.Changed} changed, {run.Gone} gone, {run.Returned} served again)")
            : string.Create(CultureInfo.InvariantCulture, $"checked {run.MissingChecked} expected id(s) in storage");
        var findings = InventoryReport.Findings(run.FindingsJson);
        return findings is null
            ? read
            : string.Create(CultureInfo.InvariantCulture, $"{read}, {new InventoryCounts(findings).Raised} raised");
    }

    private static string OwnersLine(InventoryOwners? owners)
    {
        if (owners is null)
        {
            return "not known until a reconcile";
        }

        var identities = owners.Identities.Count == 0
            ? "none"
            : string.Join(", ", owners.Identities.Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Identity} ({o.Records} claimed)")));
        return owners.Source is null ? identities : $"{identities} ({owners.Source})";
    }

    private static JsonObject Described(InventoryState inventory) => new()
    {
        ["inventoryId"] = inventory.InventoryId,
        ["partition"] = inventory.Partition,
        ["flowName"] = inventory.FlowName,
        ["ledgerId"] = inventory.FlowId.ToString("D"),
        ["name"] = inventory.Name,
        ["kind"] = inventory.Kind,
        ["query"] = inventory.Query,
        ["read"] = inventory.ReadMode,
        ["versions"] = inventory.Versions,
        ["lastBuildRunId"] = inventory.LastBuildRunId,
        ["lastBuiltUtc"] = inventory.LastBuiltUtc,
        ["lastReconcileRunId"] = inventory.LastReconcileRunId,
        ["lastReconciledUtc"] = inventory.LastReconciledUtc,
    };

    private static JsonObject Described(InventoryRunState run)
    {
        var findings = InventoryReport.Findings(run.FindingsJson);
        var owners = InventoryReport.Owners(run.OwnersJson);
        return new JsonObject
        {
            ["inventoryRunId"] = run.InventoryRunId,
            ["runId"] = run.RunId?.ToString("D"),
            ["operation"] = run.Operation,
            ["status"] = run.Status,
            ["actor"] = run.Actor,
            ["read"] = run.ReadMode,
            ["startedUtc"] = run.StartedUtc,
            ["completedUtc"] = run.CompletedUtc,
            ["listed"] = run.Listed,
            ["pages"] = run.Pages,
            ["requests"] = run.Requests,
            ["added"] = run.Added,
            ["changed"] = run.Changed,
            ["gone"] = run.Gone,
            ["returned"] = run.Returned,
            ["missingChecked"] = run.MissingChecked,
            ["findings"] = findings is null ? null : Counts(findings, zeros: false),
            ["raised"] = findings is null ? null : new InventoryCounts(findings).Raised,
            ["owners"] = owners is null ? null : Described(owners),
            ["error"] = run.Error,
        };
    }

    private static JsonObject Described(InventoryRecordState record, IReadOnlyDictionary<Guid, string> names) => new()
    {
        ["inventoryRecordId"] = record.InventoryRecordId,
        ["targetId"] = record.TargetId,
        ["kind"] = record.Kind,
        ["version"] = record.Version,
        ["createUser"] = record.CreateUser,
        ["createTime"] = record.CreateTime,
        ["modifyUser"] = record.ModifyUser,
        ["modifyTime"] = record.ModifyTime,
        ["firstSeenUtc"] = record.FirstSeenUtc,
        ["changedUtc"] = record.ChangedUtc,
        ["goneUtc"] = record.GoneUtc,
        ["finding"] = record.Finding,
        ["findingUtc"] = record.FindingUtc,
        ["detail"] = record.Detail,
        ["ledgerFlowId"] = record.LedgerFlowId?.ToString("D"),
        ["ledger"] = record.LedgerFlowId is { } flowId && names.TryGetValue(flowId, out var name) ? name : null,
        ["deliveryKey"] = record.DeliveryKey?.ToString("D"),
        ["ledgerStatus"] = record.LedgerStatus,
        ["ledgerVersion"] = record.LedgerVersion,
        ["artifactId"] = record.ArtifactId,
        ["artifactState"] = record.ArtifactState,
    };

    private static JsonObject Described(InventoryOwners owners) => new()
    {
        ["source"] = owners.Source,
        ["identities"] = new JsonArray(owners.Identities.Select(o => (JsonNode)new JsonObject { ["identity"] = o.Identity, ["records"] = o.Records }).ToArray()),
    };

    /// <summary>The counts by finding as JSON, in the order a report lists them; with <paramref name="zeros"/>, a finding no id has too.</summary>
    private static JsonArray Counts(IReadOnlyDictionary<string, long> byFinding, bool zeros) => new(InventoryReport.Counted(byFinding)
        .Where(c => zeros || c.Count > 0)
        .Select(c => (JsonNode)new JsonObject { ["finding"] = c.Finding, ["count"] = c.Count, ["raised"] = c.Raised })
        .ToArray());

    /// <summary>Which ids of the finding a removal took, as its line says it: those picked, those of a selection, or every one.</summary>
    private static string Which(InventoryRemovalState removal)
        => removal.SelectionId is { } selection
            ? string.Create(CultureInfo.InvariantCulture, $"of selection {selection}")
            : removal.NamesIds ? "picked" : "(every one)";

    private static JsonObject Described(InventoryRemovalState removal) => new()
    {
        ["removal"] = removal.InventoryRemovalId,
        ["inventoryId"] = removal.InventoryId,
        ["runId"] = removal.RunId?.ToString("D"),
        ["actor"] = removal.Actor,
        ["finding"] = removal.Finding,
        ["scope"] = removal.Scope,
        ["namesIds"] = removal.NamesIds,
        ["selection"] = removal.SelectionId,
        ["requested"] = removal.Requested,
        ["status"] = removal.Status,
        ["startedUtc"] = removal.StartedUtc,
        ["completedUtc"] = removal.CompletedUtc,
        ["removed"] = removal.Removed,
        ["gone"] = removal.Gone,
        ["skipped"] = removal.Skipped,
        ["failed"] = removal.Failed,
        ["error"] = removal.Error,
        ["activityId"] = removal.ActivityId,
    };

    private static JsonObject Described(InventoryRemovalItemState item) => new()
    {
        ["item"] = item.InventoryRemovalItemId,
        ["removal"] = item.InventoryRemovalId,
        ["id"] = item.TargetId,
        ["version"] = item.Version,
        ["finding"] = item.Finding,
        ["outcome"] = item.Outcome,
        ["reason"] = item.Reason,
        ["ledgerFlowId"] = item.LedgerFlowId?.ToString("D"),
        ["deliveryKey"] = item.DeliveryKey?.ToString("D"),
        ["recordedUtc"] = item.RecordedUtc,
    };

    private static int Count(string? value, int fallback, int max, string option)
    {
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 1 && count <= max
            ? count
            : throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{option} '{value}' is not a count between 1 and {max}."));
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
