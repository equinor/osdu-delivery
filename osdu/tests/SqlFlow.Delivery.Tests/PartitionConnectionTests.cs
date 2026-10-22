using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A partition's own connection to OSDU (osdu/docs/reference/concepts/explorer.md, How it reaches OSDU): the platform and
/// client its configuration names, by five values, and no flow; which of them a configuration and an environment leave
/// unset; where it reaches, as a person is shown it; and an explorer read made through it with no flow file anywhere,
/// signing in with the partition's client and naming the partition on every request.
/// </summary>
public class PartitionConnectionTests
{
    /// <summary>The client secret's reference, which no other suite sets.</summary>
    private const string SecretVariable = "OSDU_PARTITION_CONNECTION_TEST_SECRET";

    private static readonly IReadOnlyDictionary<string, string> Nothing = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    [Fact]
    public void The_connection_is_the_partitions_platform_and_client_and_names_the_partition_on_every_request()
    {
        var connection = PartitionConnection.Of("dev");

        Assert.Equal(("partition dev", "dev"), (connection.Label, connection.Partition));
        Assert.Equal("${env:OSDU_URL}", connection.Target.Endpoint);
        Assert.Equal(TargetAuthType.OAuth2ClientCredentials, connection.Target.Auth.Type);
        Assert.Equal(("${env:OSDU_CLIENT_ID}", "${env:OSDU_CLIENT_SECRET}"), (connection.Target.Auth.SecondarySecretRef, connection.Target.Auth.SecretRef));
        Assert.Equal("${env:OSDU_TOKEN_URL}", connection.Target.Auth.Token!.Url);
        Assert.Equal("${env:OSDU_SCOPE}", connection.Target.Auth.Token.Body["scope"]);
        Assert.Equal("dev", connection.Target.Headers["data-partition-id"]);

        // A partition is its id written literally; a reference names none until something resolves it.
        Assert.Throws<ArgumentException>(() => PartitionConnection.Of("${env:OSDU_DATA_PARTITION}"));
        Assert.Throws<ArgumentException>(() => PartitionConnection.Of(string.Empty));
    }

    [Fact]
    public void What_neither_the_configuration_nor_the_environment_sets_is_missing_in_the_order_the_names_are_listed()
    {
        Assert.Equal(PartitionConnection.Names, PartitionConnection.Missing(Nothing, NoEnvironment));

        var configured = Values(
            ("OSDU_URL", "https://osdu-dev.example.com"),
            ("OSDU_TOKEN_URL", "https://login.example.com/token"),
            ("OSDU_SCOPE", "https://osdu-dev.example.com/.default"),
            ("OSDU_CLIENT_ID", "delivery"),
            ("OSDU_CLIENT_SECRET", "${keyvault:welldb-vault/osdu-dev-client-secret}"));
        Assert.Empty(PartitionConnection.Missing(configured, NoEnvironment));

        // The environment sets what the configuration leaves out, as a run reads it.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OSDU_SCOPE"] = "api://osdu/.default", ["OSDU_CLIENT_ID"] = "delivery" };
        Assert.Equal(
            ["OSDU_URL", "OSDU_TOKEN_URL", "OSDU_CLIENT_SECRET"],
            PartitionConnection.Missing(Nothing, name => environment.GetValueOrDefault(name)));

        // A value that is its own reference leaves the name to the environment; a blank one sets nothing.
        var handedBack = Values(("OSDU_URL", "${env:OSDU_URL}"), ("OSDU_TOKEN_URL", "  "));
        Assert.Equal(["OSDU_URL", "OSDU_TOKEN_URL", "OSDU_SCOPE", "OSDU_CLIENT_ID", "OSDU_CLIENT_SECRET"], PartitionConnection.Missing(handedBack, NoEnvironment));
        Assert.DoesNotContain("OSDU_URL", PartitionConnection.Missing(handedBack, name => name == "OSDU_URL" ? "https://osdu-dev.example.com" : null));

        // Any other reference counts as set: what it resolves to is the read's to find.
        Assert.DoesNotContain("OSDU_URL", PartitionConnection.Missing(Values(("OSDU_URL", "${env:OSDU_PLATFORM}")), NoEnvironment));
    }

    [Fact]
    public void The_endpoint_is_shown_without_the_credentials_query_or_fragment_a_url_can_carry()
    {
        Assert.Equal(
            "https://osdu-dev.example.com/base",
            PartitionConnection.EndpointShown(Values(("OSDU_URL", "https://user:secret@osdu-dev.example.com/base?code=1#top")), NoEnvironment));
        Assert.Equal("https://osdu-dev.example.com:8443/", PartitionConnection.EndpointShown(Nothing, _ => "https://osdu-dev.example.com:8443"));

        // A configured reference is shown as written, since it is resolved only when a read needs it.
        Assert.Equal("${keyvault:welldb-vault/osdu-url}", PartitionConnection.EndpointShown(Values(("OSDU_URL", "${keyvault:welldb-vault/osdu-url}")), NoEnvironment));

        // Nothing set, or nothing a platform is reached at, shows nothing.
        Assert.Null(PartitionConnection.EndpointShown(Nothing, NoEnvironment));
        Assert.Null(PartitionConnection.EndpointShown(Values(("OSDU_URL", "osdu-dev.example.com")), NoEnvironment));
        Assert.Null(PartitionConnection.EndpointShown(Values(("OSDU_URL", "ftp://osdu-dev.example.com")), NoEnvironment));
    }

    [Fact]
    public async Task A_read_through_a_partitions_connection_signs_in_with_its_client_and_reads_no_flow_file()
    {
        Environment.SetEnvironmentVariable(SecretVariable, "partition-client-secret");
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/token", _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"access_token\":\"partition-token\",\"expires_in\":600}"))
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, "{\"results\":[{\"id\":\"test:master-data--Wellbore:W1\"}],\"totalCount\":1}");
        var operation = new ExploreOperation(Engine(), handler, allowLoopback: true);

        var answered = JsonNode.Parse(await operation.ExecuteAsync(
            Task("test", ExploreOperation.SearchAction, new ExplorerSearch { Kind = "*:*:master-data--Wellbore:*" }.ToJson()), CancellationToken.None))!;

        // The answer names the connection and the partition it read.
        Assert.Equal(("partition test", "test"), (answered["connection"]!.GetValue<string>(), answered["partition"]!.GetValue<string>()));
        Assert.Equal(1, answered["answer"]!["total"]!.GetValue<long>());

        // The client signed in at the partition's token endpoint with the partition's client, secret and scope.
        var token = Assert.Single(handler.Calls, c => c.Uri.AbsolutePath == "/token");
        Assert.Contains("grant_type=client_credentials", token.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=partition-client", token.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=partition-client-secret", token.Body, StringComparison.Ordinal);
        Assert.Contains("scope=api%3A%2F%2Fosdu%2F.default", token.Body, StringComparison.Ordinal);

        // The search went to the partition's platform, in the partition, under the token.
        var search = Assert.Single(handler.Calls, c => c.Uri.AbsolutePath == "/api/search/v2/query");
        Assert.Equal("localhost", search.Uri.Host);
        Assert.Equal(("test", "Bearer partition-token"), (search.Headers["data-partition-id"], search.Headers["Authorization"]));
    }

    [Fact]
    public async Task A_payload_naming_another_connection_or_no_partition_id_is_refused_before_anything_is_read()
    {
        var handler = new FakeHttpHandler();
        var operation = new ExploreOperation(Engine(), handler, allowLoopback: true);
        var search = new ExplorerSearch().ToJson();

        var refused = await Assert.ThrowsAsync<SqlFlowException>(() => operation.ExecuteAsync(Task("test", ExploreOperation.SearchAction, search, "flow"), CancellationToken.None));
        Assert.Contains("'flow' is not what the explorer reads through", refused.Message, StringComparison.Ordinal);

        var referenced = await Assert.ThrowsAsync<SqlFlowException>(() => operation.ExecuteAsync(Task("${env:OSDU_DATA_PARTITION}", ExploreOperation.SearchAction, search), CancellationToken.None));
        Assert.Contains("is not a partition id", referenced.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Calls);
    }

    /// <summary>A read of <paramref name="partition"/> through its own connection, as the control plane hands it over: its values the payload's references.</summary>
    private static ComputeTaskPayload Task(string partition, string action, string search, string connection = ExploreOperation.PartitionConnectionValue) => new()
    {
        Operation = ExploreOperation.OperationName,
        SourceRef = partition,
        Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExploreOperation.ConnectionArgument] = connection,
            [DeliveryOperation.PartitionArgument] = partition,
            [ExploreOperation.ActionArgument] = action,
            [ExploreOperation.SearchArgument] = search,
        },
        References = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OSDU_URL"] = "http://localhost",
            ["OSDU_TOKEN_URL"] = "http://localhost/token",
            ["OSDU_SCOPE"] = "api://osdu/.default",
            ["OSDU_CLIENT_ID"] = "partition-client",
            ["OSDU_CLIENT_SECRET"] = "${env:" + SecretVariable + "}",
        },
    };

    /// <summary>The engine as a host without the module's database composes it: no ledger, no cache, no kept connections.</summary>
    private static EngineContext Engine()
    {
        var stores = Samples.Stores();
        return new EngineContext(
            new DeliveryDocumentLoader(),
            new MemoryIngestionTables(),
            new StoragePayloadFiles(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            Ledger: null,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new DefaultProtocolFactory(),
            CompositeDeliveryListener.Empty);
    }

    private static Dictionary<string, string> Values(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
}
