using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension's definition hash is what a build tells a changed declaration by: a dimension whose hash changes is loaded
/// in full, and the views reading it written again. A setting the engine learns therefore leaves the hash of a document
/// that does not use it as it was. Each baseline's hash is pinned as a build before attributes took <c>many</c>,
/// <c>decode</c>, <c>maxReferences</c> and <c>trim</c>, elements <c>nulls</c> and dimensions <c>lookupKinds</c>, kept it.
/// </summary>
public class DimensionHashBaselineTests
{
    /// <summary>The hashes origin/main kept for <see cref="DimensionHashBaselines.Documents"/> before those settings existed.</summary>
    private static readonly Dictionary<string, string> Pinned = new(StringComparer.Ordinal)
    {
        ["plain"] = "2203a2197ad599ce",
        ["labelled"] = "168775cf1cfc8de3",
        ["attributes"] = "6e93ef6057917276",
        ["settings"] = "160886a98ebafb22",
        ["elements"] = "8073a0d12685ee94",
        ["filtered-elements"] = "2603472ba0dfa253",
        ["cleaned"] = "b2fa2d48f68c5f56",
    };

    [Fact]
    public void A_dimension_declared_before_a_setting_existed_keeps_the_hash_it_was_built_with()
    {
        Assert.Equal(Pinned.Keys.Order(StringComparer.Ordinal), DimensionHashBaselines.Documents.Select(d => d.Name).Order(StringComparer.Ordinal));
        foreach (var (name, yaml) in DimensionHashBaselines.Documents)
        {
            var hash = new DeliveryDocumentLoader().ParseDimension(DimensionHashBaselines.Head + yaml, "flows/dims.yaml").Dimensions[0].DefinitionHash;
            Assert.True(string.Equals(Pinned[name], hash, StringComparison.Ordinal), $"Baseline '{name}' hashes {hash} now, and was built as {Pinned[name]}.");
        }
    }
}
