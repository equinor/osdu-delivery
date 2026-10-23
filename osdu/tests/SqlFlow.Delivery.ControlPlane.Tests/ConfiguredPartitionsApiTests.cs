using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The partitions a deployment names in its configuration (<c>Osdu:Partitions</c>), as the control plane serves them: bound
/// from the settings a deployment writes, refused at start when they hold a secret's value, registered with their values
/// once the module's database is verified, marked as configured in the API's listings and kept as configured against
/// every change through it, and the readiness that waits for a partition to work against.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class ConfiguredPartitionsApiTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public void The_partitions_bind_from_the_settings_a_deployment_writes_beside_the_module_s_other_settings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Osdu:Partitions:dev:Default"] = "true",
            ["Osdu:Partitions:dev:Description"] = "The development platform",
            ["Osdu:Partitions:dev:Properties:OSDU_URL"] = "https://osdu-dev.example.com",
            ["Osdu:Partitions:dev:Properties:OSDU_CLIENT_SECRET"] = "${keyvault:estate-vault/osdu-dev-client-secret}",
            ["Osdu:Partitions:test:Properties:OSDU_URL"] = "https://osdu-test.example.com",
        }).Build();

        var options = configuration.GetSection(ConfiguredPartitionsOptions.SectionName).Get<ConfiguredPartitionsOptions>()!;
        var configured = options.Configured();

        Assert.Equal(["dev", "test"], configured.Select(p => p.Name));
        var dev = configured[0];
        Assert.Equal((true, "The development platform"), (dev.IsDefault, dev.Description));
        Assert.Equal(["OSDU_CLIENT_SECRET", "OSDU_URL"], dev.Properties.Keys);
        Assert.Equal("https://osdu-test.example.com", configured[1].Properties["OSDU_URL"]);
        Assert.Empty(new ConfiguredPartitionsOptions().Configured());
    }

    [Fact]
    public void A_control_plane_given_a_secret_s_value_does_not_start_and_does_not_repeat_it()
    {
        var factory = new ControlPlaneAppFactory()
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("Osdu:Partitions:dev:Properties:OSDU_CLIENT_SECRET", "a-literal-secret-value");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var message = string.Join(" | ", Chain(ex).Select(e => e.Message));
        Assert.Contains("Osdu:Partitions:dev:Properties:OSDU_CLIENT_SECRET", message, StringComparison.Ordinal);
        Assert.DoesNotContain("a-literal-secret-value", message, StringComparison.Ordinal);
        Assert.Contains(Chain(ex), e => e is ControlPlaneModuleException);
        factory.Dispose();
    }

    [Fact]
    public async Task The_configured_partitions_are_there_when_the_control_plane_has_started_and_are_changed_only_by_the_configuration()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (dev, test) = ("cd" + suffix, "ct" + suffix);

        // The suites run one test at a time, so the registry is this test's while it runs.
        await ClearAsync(cs, dev, test);
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithSetting($"Osdu:Partitions:{dev}:Default", "true")
                .WithSetting($"Osdu:Partitions:{dev}:Description", "Development")
                .WithSetting($"Osdu:Partitions:{dev}:Properties:OSDU_URL", "https://osdu-dev.example.com")
                // A reference the start-up pass does not read: a vault this test cannot reach would leave its configuration
                // step failed (CentralConfigurationStepTests covers that), and the start-up never done.
                .WithSetting($"Osdu:Partitions:{dev}:Properties:OSDU_CLIENT_SECRET", "${env:OSDU_DEV_CLIENT_SECRET}")
                .WithSetting($"Osdu:Partitions:{test}:Properties:OSDU_URL", "https://osdu-test.example.com");
            using var client = factory.CreateClient();
            await WaitUntilStartedAsync(client);

            var token = await TokenAsync(client, ["read"]);
            var admin = await TokenAsync(client, ["admin"]);

            // Ready means there: both partitions registered from the configuration, the configured one the default.
            var listed = (await JsonAsync(client, token, "/api/v1/delivery/partitions")).EnumerateArray()
                .Where(p => p.GetProperty("name").GetString()!.EndsWith(suffix, StringComparison.Ordinal))
                .ToList();
            Assert.Equal([dev, test], listed.Select(p => p.GetProperty("name").GetString()));
            Assert.All(listed, p => Assert.True(p.GetProperty("configured").GetBoolean()));
            Assert.All(listed, p => Assert.Equal(ConfiguredPartitions.Actor, p.GetProperty("createdBy").GetString()));
            Assert.Equal([true, false], listed.Select(p => p.GetProperty("isDefault").GetBoolean()));

            var values = (await JsonAsync(client, token, $"/api/v1/delivery/config?partition={dev}")).EnumerateArray().ToList();
            Assert.Equal(["OSDU_CLIENT_SECRET", "OSDU_URL"], values.Select(v => v.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
            Assert.All(values, v => Assert.True(v.GetProperty("configured").GetBoolean()));

            // Every change of what the configuration sets is refused, naming where to change it.
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Delete, $"/api/v1/delivery/partitions/{test}", null),
                (HttpMethod.Put, $"/api/v1/delivery/partitions/{dev}", new DeliveryPartitionDescribeRequest("mine")),
                (HttpMethod.Post, $"/api/v1/delivery/partitions/{test}/default", null),
                (HttpMethod.Post, "/api/v1/delivery/partitions", new DeliveryPartitionAddRequest("x" + suffix, null, true)),
                (HttpMethod.Put, $"/api/v1/delivery/config/OSDU_URL?partition={dev}", new DeliveryConfigSetRequest("https://elsewhere.example.com", null)),
                (HttpMethod.Delete, $"/api/v1/delivery/config/OSDU_CLIENT_SECRET?partition={dev}", null),
            })
            {
                using var refused = await SendAsync(client, admin, method, path, body);
                var problem = await refused.Content.ReadAsStringAsync();
                Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"{method} {path} answered {(int)refused.StatusCode}: {problem}");
                Assert.Contains("Set by configuration", problem, StringComparison.Ordinal);
                Assert.Contains("Osdu:Partitions:", problem, StringComparison.Ordinal);
            }

            var after = (await JsonAsync(client, token, "/api/v1/delivery/partitions")).EnumerateArray()
                .Single(p => p.GetProperty("name").GetString() == dev);
            Assert.Equal(("Development", true), (after.GetProperty("description").GetString(), after.GetProperty("isDefault").GetBoolean()));
        }
        finally
        {
            await ClearAsync(cs, dev, test);
        }
    }

    [Fact]
    public async Task A_control_plane_with_no_partition_serves_and_reports_its_start_up_as_not_done_until_one_is_registered()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var partition = "r" + Guid.NewGuid().ToString("N")[..10];
        await ClearAsync(cs, partition);
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();

            // Written, and still nothing to work against: its start-up not done for as long as it stays so, while it keeps
            // serving, so a person can read why and register one.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using var notStarted = await client.GetAsync(new Uri("/health/startup", UriKind.Relative));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, notStarted.StatusCode);
                using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                await Task.Delay(500);
            }

            await new DeliveryPartitionRegistry(() => SampleEstate.Context(cs)).AddAsync(partition, null, makeDefault: false, "tests", DateTime.UtcNow);
            await WaitUntilStartedAsync(client);
        }
        finally
        {
            await ClearAsync(cs, partition);
        }
    }

    private static async Task ClearAsync(string cs, params string[] partitions)
    {
        await using var osdu = SampleEstate.Context(cs);
        await osdu.DeliveryPartitions.ExecuteDeleteAsync();
        await osdu.DeliveryConfigProperties.Where(p => p.RepoId == null && partitions.Contains(p.Partition!)).ExecuteDeleteAsync();
    }

    private static async Task WaitUntilStartedAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow + Patience;
        HttpStatusCode last;
        do
        {
            using var started = await client.GetAsync(new Uri("/health/startup", UriKind.Relative));
            last = started.StatusCode;
            if (last == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        // The probe answers anyone and names nothing; the pass itself says which step is not done, and why.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/bootstrap", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(client, ["read"]));
        using var pass = await client.SendAsync(request);
        Assert.Fail($"The control plane did not report its start-up done within {Patience.TotalSeconds}s (last {(int)last}): {await pass.Content.ReadAsStringAsync()}");
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes.ToArray()));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
