using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The hash a flow of every kind that keeps a ledger is paired by (<see cref="DeliveryDocumentHash"/>,
/// osdu/docs/reference/concepts/renaming-a-pipeline.md): one document under two names hashes the same, whatever its name
/// derives (a retrieval flow's id), and a document changed beyond its name hashes otherwise.
/// </summary>
public sealed class DeliveryDocumentHashTests
{
    public static TheoryData<string, string> Flows => new()
    {
        { LedgerKinds.Retrieval, Samples.RetrievalFlowFile },
        { LedgerKinds.Assertion, Path.Combine(Samples.Source, "flows", "welldb-well-04-assertion.yaml") },
        { LedgerKinds.Dimension, Path.Combine(Samples.Source, "flows", "welldb-welllog-05-dimensions.yaml") },
        { LedgerKinds.Inventory, Path.Combine(Samples.Source, "flows", "welldb-osdu-06-inventory.yaml") },
    };

    [Theory]
    [MemberData(nameof(Flows))]
    public void One_document_under_two_names_hashes_the_same_and_a_changed_one_otherwise(string kind, string file)
    {
        var yaml = File.ReadAllText(file).ReplaceLineEndings("\n");
        var name = Path.GetFileNameWithoutExtension(file);
        var documents = new DeliveryDocumentLoader();

        var original = DeliveryCatalogSync.DescribeFlow(documents, kind, yaml, "flows/a.yaml");
        var renamed = DeliveryCatalogSync.DescribeFlow(
            documents, kind, yaml.Replace($"name: {name}\n", $"name: {name}-renamed\n", StringComparison.Ordinal), "elsewhere/b.yaml");
        var commented = DeliveryCatalogSync.DescribeFlow(
            documents, kind, yaml.Replace($"name: {name}\n", $"name: {name}\n# A comment and a blank line change nothing.\n\n", StringComparison.Ordinal), "flows/a.yaml");
        var edited = yaml.Contains("\ndescription: ", StringComparison.Ordinal)
            ? yaml.Replace("\ndescription: ", "\ndescription: Edited. ", StringComparison.Ordinal)
            : yaml.Replace($"name: {name}\n", $"name: {name}\ndescription: Edited.\n", StringComparison.Ordinal);
        var changed = DeliveryCatalogSync.DescribeFlow(documents, kind, edited, "flows/a.yaml");

        Assert.Equal((kind, name, DeliveryDocumentHash.Length), (original.Kind, original.Name, original.DocumentHash.Length));
        Assert.Equal($"{name}-renamed", renamed.Name);
        Assert.Equal(original.DocumentHash, renamed.DocumentHash);
        Assert.Equal(original.DocumentHash, commented.DocumentHash);
        Assert.NotEqual(original.DocumentHash, changed.DocumentHash);
    }

    [Fact]
    public void A_delivery_document_hashes_the_same_on_every_machine()
    {
        // Read with the line endings the repository keeps (LF), whatever the checkout made of them. The value is the one this
        // machine and the CI runners compute alike, whatever their processor count: what a node derives for itself (its render
        // threads) is no part of the document.
        var source = new DeliveryDocumentLoader().ParseSource(File.ReadAllText(Samples.Flow).ReplaceLineEndings("\n"), Samples.Flow);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal) { ["WellLog@1.4.0"] = "mapping-hash" };

        Assert.Equal(SampleFlowHash, DeliveryDocumentHash.Of(source, mappings));
        Assert.DoesNotContain(
            "EffectiveRenderThreads",
            System.Text.Json.JsonSerializer.Serialize(source.Interfaces[0].Parallelism),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The document hash of the sample well log flow, read with LF line endings.</summary>
    internal const string SampleFlowHash = "a8c30f716110ea2279665928dedddcc9818ba9b34d0dd386819369e37f25b07a";
}
