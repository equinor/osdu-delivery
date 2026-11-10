using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Captures a cached type that holds a dimension's values (osdu/docs/reference/flow/cache.md, "Dimension values
/// (dimension, dimensionFlow)"): the values the dimension's last build wrote in the partition, each a lookup row keyed
/// by the value, with the keys it stands for as a set (so a mapping finds the value of any key, as a lookup matches a
/// set on any one of its values), the records holding them, the search filter
/// finding them, and the attributes the type names (so a mapping finds a key's attribute the same way). Everything is read from the ledger, a page at a time; nothing is asked of OSDU, since the dimension flow's
/// build already read it.
/// </summary>
internal static class DimensionCapture
{
    /// <summary>
    /// The most originals one type holds across its members: every row is loaded with the cache version a render reads, so a
    /// dimension beyond this is filtered through its members, not held whole as a lookup table.
    /// </summary>
    public const int MaxOriginals = 500_000;

    /// <summary>The members whose originals one ledger read takes.</summary>
    private const int OriginalsChunk = 500;

    /// <summary>The most values of one attribute a cached value carries: a value standing for many keys holds them as a set.</summary>
    private const int MaxAttributeValues = 1000;

    /// <summary>The members of the dimension <paramref name="type"/> holds, in <paramref name="partition"/>, as a lookup table.</summary>
    public static async Task<ReferenceType> CaptureAsync(ILedger ledger, string partition, ReferenceTypeSpec type, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(log);
        var dimension = await FindAsync(ledger, partition, type, ct).ConfigureAwait(false);
        var carried = type.Fields.Skip(DimensionColumns.Fields.Count).Select(f => f.Name).ToList();
        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        var unread = carried.Where(name => declared.All(a => !string.Equals(a.Name, name, StringComparison.Ordinal))).ToList();
        if (unread.Count > 0)
        {
            throw new DeliveryException(
                $"Cached type '{type.Name}' carries {string.Join(", ", unread)}, which dimension {dimension.Name} of {dimension.FlowName} does not read as it was last built{(declared.Count == 0 ? " (it reads no attribute)" : $" (it reads {string.Join(", ", declared.Select(a => a.Name))})")}. Name an attribute the dimension declares, exactly, and build it.");
        }

        if (dimension.Members > LookupKeys.MaxEntries || dimension.Originals > MaxOriginals)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"Cached type '{type.Name}' holds dimension {dimension.Name} of {dimension.FlowName}, whose {dimension.Members} value(s) and {dimension.Originals} key(s) are more than a dimension type holds ({LookupKeys.MaxEntries} values, {MaxOriginals} keys): every row is loaded with each render. Narrow the dimension with its query, or filter by its values instead."));
        }

        var members = new List<DimensionMemberState>((int)Math.Min(dimension.Members, LookupKeys.MaxEntries));
        DimensionMemberCursor? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListDimensionMembersAsync(
                dimension.DimensionId, new DimensionMemberQuery(null, false, after, OsduLedger.MaxDimensionPage), ct).ConfigureAwait(false);
            members.AddRange(page);
            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                break;
            }

            after = new DimensionMemberCursor(page[^1].Value, page[^1].Records);
        }

        var originals = new Dictionary<long, List<string>>(members.Count);
        foreach (var chunk in members.Select(m => m.MemberId).Chunk(OriginalsChunk))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var original in await ledger.MemberOriginalsAsync(dimension.DimensionId, chunk, ct).ConfigureAwait(false))
            {
                if (original.MemberId is { } memberId)
                {
                    (originals.TryGetValue(memberId, out var list) ? list : originals[memberId] = []).Add(original.Original);
                }
            }
        }

        // The attributes each value carries: every value its keys hold, a set when there are several.
        var attributes = new Dictionary<long, IReadOnlyList<DimensionMemberAttributeValue>>();
        if (carried.Count > 0)
        {
            foreach (var chunk in members.Select(m => m.MemberId).Chunk(OriginalsChunk))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var held in await ledger.MemberAttributesAsync(dimension.DimensionId, chunk, MaxAttributeValues, ct).ConfigureAwait(false))
                {
                    attributes[held.MemberId] = held.Attributes;
                }
            }
        }

        var rows = members.Select(member =>
        {
            var gathered = originals.TryGetValue(member.MemberId, out var list) ? list : [];
            var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                [DimensionColumns.Value] = ReferenceValue.Of(member.Value),
                [DimensionColumns.Records] = ReferenceValue.Of(member.Records.ToString(CultureInfo.InvariantCulture)),
            };
            if (gathered.Count > 0)
            {
                fields[DimensionColumns.Keys] = ReferenceValue.OfMany(gathered.Order(StringComparer.Ordinal).Select(o => (JsonNode)JsonValue.Create(o)).ToList());
            }

            if (member.Filter is { } filter)
            {
                fields[DimensionColumns.Filter] = ReferenceValue.Of(filter);
            }

            foreach (var name in carried)
            {
                var values = (attributes.TryGetValue(member.MemberId, out var held) ? held : [])
                    .Where(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                    .Select(a => a.Value)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (values.Count == 1)
                {
                    fields[name] = ReferenceValue.Of(values[0]);
                }
                else if (values.Count > 1)
                {
                    fields[name] = ReferenceValue.OfMany(values.Select(v => (JsonNode)JsonValue.Create(v)).ToList());
                }
            }

            return new ReferenceItem(member.Value, fields);
        });

        var captured = new ReferenceType(type.Name, ReferenceType.LookupEntityType(type.Name), rows.OrderBy(r => r.Id, StringComparer.Ordinal), DimensionColumns.Value);
        log.LogInformation(
            "Captured {Count} {Type} row(s) from dimension {Dimension} of {Flow} as its build {Build} left it, with {Keys} key(s).",
            members.Count, type.Name, dimension.Name, dimension.FlowName, dimension.LastRunId, originals.Values.Sum(l => l.Count));
        return captured;
    }

    /// <summary>
    /// The dimension a type holds, as the partition's ledger keeps it: the one its dimension flow declares under the name,
    /// which a completed build has written.
    /// </summary>
    /// <exception cref="DeliveryException">No build of the dimension has completed in the partition.</exception>
    public static async Task<DimensionState> FindAsync(ILedger ledger, string partition, ReferenceTypeSpec type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(type);
        var kept = await ledger.ListDimensionsAsync(partition, null, ct).ConfigureAwait(false);
        var dimension = kept.FirstOrDefault(d => string.Equals(d.FlowName, type.DimensionFlow, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Name, type.Dimension, StringComparison.OrdinalIgnoreCase));
        if (dimension is null || dimension.LastRunId is null)
        {
            throw new DeliveryException(
                $"Cached type '{type.Name}' holds dimension {type.Dimension} of dimension flow {type.DimensionFlow}, which has no completed build in partition '{partition}', so nothing was captured. Build it first: sqlflow run <{type.DimensionFlow}.yaml> --payload '{{\"dimensions\":[\"{type.Dimension}\"]}}'.");
        }

        return dimension;
    }
}
