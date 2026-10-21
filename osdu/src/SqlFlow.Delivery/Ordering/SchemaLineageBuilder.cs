using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Ordering;

/// <summary>
/// Builds OSDU's lineage for a release of the OSDU data definitions and keeps it (osdu/docs/reference/concepts/osdu-order.md,
/// OSDU's lineage by release): every record kind the release publishes, its schema bundled as a template file holds it,
/// and the properties of that schema that refer to other records. A kind whose schema cannot be read is listed with why,
/// and the rest are kept: one broken file of a release never costs the lineage of the others.
/// </summary>
public sealed class SchemaLineageBuilder : IDisposable
{
    /// <summary>How many kinds are read at once: the files are on disk once the release is, and reading is cheap.</summary>
    public const int Parallelism = 4;

    private readonly OsduDataDefinitions _definitions;
    private readonly SchemaLineageStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>One build at a time: two builds of one release would each replace what the other kept.</summary>
    private readonly SemaphoreSlim _building = new(1, 1);

    public SchemaLineageBuilder(OsduDataDefinitions definitions, SchemaLineageStore store, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _definitions = definitions;
        _store = store;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Builds and keeps the lineage of <paramref name="release"/> (the newest release when none is named), replacing an
    /// earlier build of it, and returns what was kept. The release is downloaded first when the local copy lacks it.
    /// </summary>
    public async Task<SchemaLineageInfo> BuildAsync(string? release, string builtBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(builtBy);
        await _building.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await BuildOneAsync(release, builtBy, ct).ConfigureAwait(false);
        }
        finally
        {
            _building.Release();
        }
    }

    public void Dispose() => _building.Dispose();

    private async Task<SchemaLineageInfo> BuildOneAsync(string? release, string builtBy, CancellationToken ct)
    {
        var index = await _definitions.IndexAsync(release, ct).ConfigureAwait(false);
        var name = index.Release.Name;
        var kinds = new ConcurrentBag<SchemaLineageEntry>();
        var failures = new ConcurrentBag<SchemaLineageFailure>();
        await Parallel.ForEachAsync(
            index.Schemas,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (schema, token) =>
            {
                try
                {
                    var file = await _definitions.FetchAsync(name, schema.Kind, token).ConfigureAwait(false);
                    var template = OsduTemplate.From(file.Schema);
                    if (OrderDeliveries.Node(template) is { } node)
                    {
                        kinds.Add(new SchemaLineageEntry(node, schema.Version, schema.Status));
                    }
                    else
                    {
                        failures.Add(new SchemaLineageFailure(schema.Kind, "its kind names no entity type"));
                    }
                }
                catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or ArgumentException or IOException)
                {
                    failures.Add(new SchemaLineageFailure(schema.Kind, SecretHygiene.RedactedMessage(ex.Message)));
                }
            }).ConfigureAwait(false);

        var ordered = kinds.OrderBy(k => k.Node.Kind, StringComparer.Ordinal).ToList();
        var failed = failures.OrderBy(f => f.Kind, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
        {
            throw new DeliveryException(
                $"No kind of release {name} could be read ({failed.Count} failed), so there is no lineage to keep."
                + (failed.Count > 0 ? $" The first: {failed[0].Kind}: {failed[0].Reason}" : string.Empty));
        }

        var info = await _store.SaveAsync(
            name, index.Release.Commit, index.Release.PublishedUtc?.UtcDateTime, ordered, failed, builtBy, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Built OSDU's lineage of release {Release} ({Commit}): {Kinds} kind(s), {Relationships} relationship(s), {Failed} kind(s) not read.",
            info.Release, info.Commit, info.Kinds, info.Relationships, failed.Count);
        return info;
    }
}
