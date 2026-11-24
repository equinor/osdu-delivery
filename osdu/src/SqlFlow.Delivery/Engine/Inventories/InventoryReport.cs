using System.Text.Json;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>A finding of an inventory with how many of its ids have it, and whether a report raises it.</summary>
public sealed record InventoryFindingCount(string Finding, long Count, bool Raised);

/// <summary>
/// The owners a reconcile of an inventory used (osdu/docs/reference/flow/inventory.md, Owners): how it knew them
/// (<c>declared</c> by the flow, <c>inferred</c> from the ids a ledger claims, or <c>none</c>), and each identity with
/// how many of the ids a ledger claims it created.
/// </summary>
public sealed record InventoryOwners(string? Source, IReadOnlyList<InventoryOwner> Identities);

/// <summary>
/// An inventory's newest run (one under way, or one that failed after the last that completed), its last completed build, and
/// the run its last reconcile was, whose counts by finding are what a listing shows; each null when there is none.
/// </summary>
public sealed record InventoryRecentRuns(InventoryRunState? Latest, InventoryRunState? LastBuild, InventoryRunState? LastReconcile);

/// <summary>
/// What an inventory's report reads back of what its runs kept: the counts by finding a run wrote, the owners a reconcile used,
/// and every finding counted in the order a report lists them. The control plane's answers and the CLI's output read through
/// here, so the two say the same.
/// </summary>
public static class InventoryReport
{
    /// <summary>The most identities a report reads back of one run's owners.</summary>
    private const int MaxOwners = 500;

    /// <summary>
    /// The inventory's newest run, last build and last reconcile, each read once: a build that completed is usually all three.
    /// </summary>
    public static async Task<InventoryRecentRuns> RecentRunsAsync(ILedger ledger, InventoryState inventory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(inventory);
        if (string.IsNullOrWhiteSpace(inventory.Partition))
        {
            return new InventoryRecentRuns(null, null, null);
        }

        var newest = await ledger.Inventories.ListRunsAsync(inventory.Partition, inventory.InventoryId, 1, ct).ConfigureAwait(false);
        var read = new Dictionary<long, InventoryRunState?>();
        if (newest.Count > 0)
        {
            read[newest[0].InventoryRunId] = newest[0];
        }

        async Task<InventoryRunState?> RunAsync(long? inventoryRunId)
        {
            if (inventoryRunId is not { } id)
            {
                return null;
            }

            if (!read.TryGetValue(id, out var run))
            {
                run = await ledger.Inventories.GetRunAsync(inventory.Partition, id, ct).ConfigureAwait(false);
                read[id] = run;
            }

            return run;
        }

        var lastBuild = await RunAsync(inventory.LastBuildRunId).ConfigureAwait(false);
        var lastReconcile = await RunAsync(inventory.LastReconcileRunId).ConfigureAwait(false);
        return new InventoryRecentRuns(newest.Count > 0 ? newest[0] : null, lastBuild, lastReconcile);
    }

    /// <summary>
    /// The owners a run or an inventory keeps (<paramref name="json"/>, as the runner writes it: <c>{"source":..,"owners":[{"identity":..,"records":..}]}</c>),
    /// with <paramref name="source"/> standing for how they were known when the JSON says nothing of it. Null when nothing is
    /// kept, or what is kept is not that shape.
    /// </summary>
    public static InventoryOwners? Owners(string? json, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return source is null ? null : new InventoryOwners(source, []);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var known = root.TryGetProperty("source", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null;
            var identities = new List<InventoryOwner>();
            if (root.TryGetProperty("owners", out var owners) && owners.ValueKind == JsonValueKind.Array)
            {
                foreach (var owner in owners.EnumerateArray())
                {
                    if (identities.Count >= MaxOwners)
                    {
                        break;
                    }

                    if (owner.ValueKind == JsonValueKind.Object
                        && owner.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.String
                        && identity.GetString() is { Length: > 0 } name)
                    {
                        var records = owner.TryGetProperty("records", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var n) ? n : 0;
                        identities.Add(new InventoryOwner(name, records));
                    }
                }
            }

            return new InventoryOwners(known ?? source, identities);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The counts by finding a run wrote (<paramref name="json"/>: <c>{"orphan":3,"tracked":120}</c>); null when the run wrote
    /// none (a build that failed before it reconciled), or what it kept is not that shape.
    /// </summary>
    public static IReadOnlyDictionary<string, long>? Findings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var count) && count >= 0)
                {
                    counts[property.Name] = count;
                }
            }

            return counts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every finding with its count, in the order a report lists them, a finding no id has counted as zero; a finding the
    /// counts hold that this code does not know (written by a newer one) follows, so nothing counted is left out.
    /// </summary>
    public static IReadOnlyList<InventoryFindingCount> Counted(IReadOnlyDictionary<string, long> byFinding)
    {
        ArgumentNullException.ThrowIfNull(byFinding);
        var counted = InventoryFindings.All
            .Select(f => new InventoryFindingCount(f, byFinding.GetValueOrDefault(f), InventoryFindings.Raised.Contains(f, StringComparer.Ordinal)))
            .ToList();
        counted.AddRange(byFinding.Keys
            .Where(f => !InventoryFindings.IsKnown(f))
            .Order(StringComparer.Ordinal)
            .Select(f => new InventoryFindingCount(f, byFinding[f], false)));
        return counted;
    }
}
