using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Engine.Operations;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Partitions an API test registers with the catalog for the explorer to read, each with the values its own connection is
/// made of set at the control plane's scope for it, as a deployment's configuration sets them
/// (osdu/docs/reference/concepts/explorer.md, How it reaches OSDU); and taken out again with every value set for them.
/// The platform they name is one no test reaches: the operations are stood in for.
/// </summary>
internal static class TestPartitions
{
    /// <summary>The values <paramref name="partition"/>'s connection is made of; the client secret a reference, as a configuration holds it.</summary>
    public static IReadOnlyDictionary<string, string> ConnectionValues(string partition) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PartitionConnection.UrlName] = $"https://osdu-{partition}.example.com",
        [PartitionConnection.TokenUrlName] = "https://login.example.com/oauth2/token",
        [PartitionConnection.ScopeName] = $"https://osdu-{partition}.example.com/.default",
        [PartitionConnection.ClientIdName] = "explorer-tests",
        [PartitionConnection.ClientSecretName] = "${keyvault:explorer-tests/osdu-client-secret}",
    };

    /// <summary>
    /// Registers <paramref name="partition"/>, not as the default, and sets its connection's values unless
    /// <paramref name="connected"/> is false.
    /// </summary>
    public static async Task RegisterAsync(string connectionString, string partition, bool connected = true)
    {
        var now = DateTime.UtcNow;
        await new DeliveryPartitionRegistry(() => SampleEstate.Context(connectionString)).AddAsync(partition, null, makeDefault: false, "tests", now);
        if (!connected)
        {
            return;
        }

        var store = new DeliveryConfigStore(() => SampleEstate.Context(connectionString));
        foreach (var (name, value) in ConnectionValues(partition))
        {
            await store.SetAsync(null, partition, name, value, null, "tests", now, null);
        }
    }

    /// <summary>Takes <paramref name="partitions"/> out of the registry, with every value set for them at any scope.</summary>
    public static async Task ForgetAsync(string connectionString, params string[] partitions)
    {
        await using var osdu = SampleEstate.Context(connectionString);
        await osdu.DeliveryConfigProperties.Where(p => p.Partition != null && partitions.Contains(p.Partition)).ExecuteDeleteAsync();
        await osdu.DeliveryPartitions.Where(p => partitions.Contains(p.Name)).ExecuteDeleteAsync();
    }
}
