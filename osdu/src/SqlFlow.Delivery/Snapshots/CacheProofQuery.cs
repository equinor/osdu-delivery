namespace SqlFlow.Delivery.Snapshots;

/// <summary>What a proof asks of a partition's cache (<see cref="CacheProofQuery"/>).</summary>
public enum CacheProofKind
{
    /// <summary>Records of the types under the ids looked for, exactly, or once case is ignored, or holding the code an id ends with.</summary>
    Ids,

    /// <summary>Records of the types holding each value looked for at the fields compared, exactly or once case is ignored, or at any field.</summary>
    Values,

    /// <summary>The records named, with every field the cache holds of them.</summary>
    Records,

    /// <summary>The types the version holds, with how many records each.</summary>
    Types,
}

/// <summary>One statement of a finding's proof that reads a partition's cache (osdu/docs/reference/concepts/value-proof.md).</summary>
public sealed record CacheProofQuery
{
    public required CacheProofKind Kind { get; init; }

    /// <summary>The partition whose cache is read.</summary>
    public required string Scope { get; init; }

    /// <summary>The version read: the one the check rendered with.</summary>
    public required string Version { get; init; }

    /// <summary>The cached types read, by short name.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>For <see cref="CacheProofKind.Values"/>, the fields the values were compared with.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>The ids, values or records looked for, as the render compared them.</summary>
    public IReadOnlyList<string> Looked { get; init; } = [];

    /// <summary>The most rows read.</summary>
    public int Limit { get; init; } = 200;
}
