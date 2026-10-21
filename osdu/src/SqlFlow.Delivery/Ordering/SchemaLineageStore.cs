using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Order;

namespace SqlFlow.Delivery.Ordering;

/// <summary>A kind a release lists that its lineage could not read, and why.</summary>
public sealed record SchemaLineageFailure(string Kind, string Reason);

/// <summary>What a built lineage of a release says of itself.</summary>
public sealed record SchemaLineageInfo(
    string Release,
    string Commit,
    DateTime? PublishedUtc,
    DateTime BuiltUtc,
    string BuiltBy,
    int Kinds,
    int Relationships,
    IReadOnlyList<SchemaLineageFailure> Failures);

/// <summary>A kind of a release's lineage as the store holds it: its schema node, its version and its status in the release.</summary>
public sealed record SchemaLineageEntry(SchemaNode Node, string Version, string? Status);

/// <summary>A built lineage of a release: what it says of itself, and every kind it holds.</summary>
public sealed record SchemaLineageGraph(SchemaLineageInfo Info, IReadOnlyList<SchemaLineageEntry> Kinds)
{
    /// <summary>The kinds as the OSDU order reads schemas.</summary>
    public OsduSchemaGraph Graph() => OsduSchemaGraph.Of(Kinds.Select(k => k.Node));
}

/// <summary>
/// OSDU's lineage by release, kept in the module database (<c>[osdu].[SchemaLineage]</c> and
/// <c>[osdu].[SchemaLineageKind]</c>; osdu/docs/reference/concepts/osdu-order.md, OSDU's lineage by release): every kind a
/// release of the OSDU data definitions publishes, with the properties of its schema that refer to other records. A
/// release is built once and kept, so any release can be read back without reading the data definitions again, and the
/// OSDU order reads a kind's relationships from the newest built release holding it when no template folder holds its template.
/// </summary>
public sealed class SchemaLineageStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Func<OsduDbContext> _contexts;

    public SchemaLineageStore(Func<OsduDbContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <summary>Every built release's lineage, newest built first.</summary>
    public async Task<IReadOnlyList<SchemaLineageInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = _contexts();
        var rows = await db.SchemaLineages.AsNoTracking().OrderByDescending(l => l.BuiltUtc).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(Info)];
    }

    /// <summary>The lineage of <paramref name="release"/>, or null when it has not been built.</summary>
    public async Task<SchemaLineageGraph?> LoadAsync(string release, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        await using var db = _contexts();
        var row = await db.SchemaLineages.AsNoTracking().FirstOrDefaultAsync(l => l.Release == release.Trim(), ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var kinds = await db.SchemaLineageKinds.AsNoTracking()
            .Where(k => k.LineageId == row.Id)
            .OrderBy(k => k.Kind)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new SchemaLineageGraph(Info(row), [.. kinds.Select(Entry)]);
    }

    /// <summary>
    /// The schema node of <paramref name="kind"/> from the newest built release holding it, or null when no built release
    /// does. A kind's schema is the same in every release that publishes it, so any release holding it answers.
    /// </summary>
    public async Task<SchemaNode?> FindKindAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        await using var db = _contexts();
        return await FindKindAsync(db, kind, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="FindKindAsync(string, CancellationToken)"/> on <paramref name="db"/>: the context a caller already holds,
    /// such as the repository sync's, enlisted in its transaction.
    /// </summary>
    public static async Task<SchemaNode?> FindKindAsync(OsduDbContext db, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var wanted = kind.Trim();
        var row = await db.SchemaLineageKinds.AsNoTracking()
            .Where(k => k.Kind == wanted)
            .Join(db.SchemaLineages, k => k.LineageId, l => l.Id, (k, l) => new { Kind = k, l.BuiltUtc })
            .OrderByDescending(x => x.BuiltUtc)
            .Select(x => x.Kind)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return row is null || !string.Equals(row.Kind, wanted, StringComparison.Ordinal) ? null : Entry(row).Node;
    }

    /// <summary>
    /// Keeps the lineage of <paramref name="release"/>: replaces whatever an earlier build of the release kept, in one
    /// transaction, so a reader sees either the old lineage or the new one whole.
    /// </summary>
    public async Task<SchemaLineageInfo> SaveAsync(
        string release,
        string commit,
        DateTime? publishedUtc,
        IReadOnlyList<SchemaLineageEntry> kinds,
        IReadOnlyList<SchemaLineageFailure> failures,
        string builtBy,
        DateTime builtUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentException.ThrowIfNullOrWhiteSpace(builtBy);
        var name = release.Trim();
        if (name.Length > SchemaLineage.MaxReleaseLength)
        {
            throw new DeliveryException($"The release name '{name[..40]}...' is longer than the {SchemaLineage.MaxReleaseLength} characters a lineage keeps.");
        }

        var duplicate = kinds.GroupBy(k => k.Node.Kind, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new DeliveryException($"The lineage of {name} lists {duplicate.Key} more than once.");
        }

        await using var db = _contexts();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var row = await db.SchemaLineages.AsTracking().FirstOrDefaultAsync(l => l.Release == name, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new SchemaLineage { Id = Guid.NewGuid(), Release = name };
                db.SchemaLineages.Add(row);
            }
            else
            {
                var id = row.Id;
                await db.SchemaLineageKinds.Where(k => k.LineageId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            row.Commit = Clip(commit.Trim(), 64);
            row.PublishedUtc = publishedUtc;
            row.BuiltUtc = builtUtc;
            row.BuiltBy = Clip(builtBy, 200);
            row.Kinds = kinds.Count;
            row.Relationships = kinds.Sum(k => k.Node.Relationships.Count);
            row.FailuresJson = JsonSerializer.Serialize(failures, Json);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var chunk in kinds.Chunk(500))
            {
                db.SchemaLineageKinds.AddRange(chunk.Select(k => new SchemaLineageKind
                {
                    LineageId = row.Id,
                    Kind = k.Node.Kind,
                    EntityType = k.Node.EntityType,
                    Version = Clip(k.Version, 64),
                    Status = k.Status is null ? null : Clip(k.Status, 32),
                    RelationshipsJson = JsonSerializer.Serialize(k.Node.Relationships.Select(r => new StoredRelationship(r.Property, [.. r.Targets])), Json),
                }));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return Info(row);
        }).ConfigureAwait(false);
    }

    private static SchemaLineageInfo Info(SchemaLineage row)
        => new(row.Release, row.Commit, row.PublishedUtc, row.BuiltUtc, row.BuiltBy, row.Kinds, row.Relationships, Failures(row.FailuresJson));

    private static IReadOnlyList<SchemaLineageFailure> Failures(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SchemaLineageFailure>>(json, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A lineage's failures are not the JSON array the store writes: {ex.Message}", ex);
        }
    }

    private static SchemaLineageEntry Entry(SchemaLineageKind row)
    {
        List<StoredRelationship> relationships;
        try
        {
            relationships = JsonSerializer.Deserialize<List<StoredRelationship>>(row.RelationshipsJson, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The relationships kept for {row.Kind} are not the JSON array the store writes: {ex.Message}", ex);
        }

        var node = new SchemaNode(row.Kind, row.EntityType, [.. relationships.Select(r => new SchemaRelationship(r.Property, r.Targets))]);
        return new SchemaLineageEntry(node, row.Version, row.Status);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    private sealed record StoredRelationship(string Property, List<string> Targets);
}
