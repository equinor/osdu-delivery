using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// One value a sampled type keeps of each record: the name it is cached under, the path of the record it is read at (an
/// attribute's definition, for one), whether it is an attribute, and how many of the sampled records gave it a value.
/// </summary>
public sealed record CacheSampleColumn(string Name, string Path, bool Attribute, int Held);

/// <summary>One sampled record as the cache would keep it: its id, and each value it gave under the name it is cached under.</summary>
public sealed record CacheSampleRecord(string Id, JsonObject Fields);

/// <summary>
/// The first records of a cached type as the cache would keep them (<see cref="SnapshotBuilder.SampleTypeAsync"/>): the
/// name a mapping reads the type by, its entity type, how many records the kind (and the query) holds, what is kept of
/// each, the records sampled, what the reads had to say, and the search service's own words when it refused the query.
/// </summary>
public sealed record CacheSample(
    string Name, string EntityType, long Total, IReadOnlyList<CacheSampleColumn> Columns, IReadOnlyList<CacheSampleRecord> Records,
    IReadOnlyList<string> Notes, string? Refusal);
