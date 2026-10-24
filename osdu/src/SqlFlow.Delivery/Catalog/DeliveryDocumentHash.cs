using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// What a delivery pipeline is apart from its name and the file it is written in: SHA-256, in lowercase hex, of its
/// interfaces as parsed, with the flow's name and path left out, and of the mapping document each interface pins, by the
/// content hash the repository sync recorded of it (osdu/docs/reference/concepts/renaming-a-pipeline.md). Two pipelines with
/// one hash are one pipeline under two names, which is how the repository sync tells a renamed pipeline from a new one, and
/// what renders a delivery is part of what it is. The flow is hashed as parsed, so its comments and layout play no part.
/// </summary>
public static class DeliveryDocumentHash
{
    /// <summary>The length of a hash: 64 hex digits.</summary>
    public const int Length = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The hash of <paramref name="source"/> with its name and path left out, and of the mapping each interface pins, by its
    /// reference in <paramref name="mappings"/> (the content hash of each mapping document the repository holds); a mapping
    /// the repository does not hold counts as none.
    /// </summary>
    public static string Of(SourceDefinition source, IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mappings);
        var unnamed = new
        {
            source.ParallelInterfaces,
            Interfaces = source.Interfaces.Select(f => f with { Name = string.Empty, SourcePath = null }).ToList(),
            Mappings = source.Interfaces.Select(f => mappings.GetValueOrDefault(f.Render.Mapping)).ToList(),
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unnamed, Json)));
    }
}
