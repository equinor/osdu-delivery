using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// <c>GET /api/v1/environments</c> through the in-memory host: the environments as the deployment's settings name them
/// (environment variables bind them as <c>ControlPlane__Environments__Links__0__Name</c>), in their order, the current one
/// marked; nothing when none is configured; only to a signed-in caller; and a mistake in the list never stops the host,
/// which leaves it out and logs why.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EnvironmentsApiTests
{
    private static ControlPlaneAppFactory Estate(string current)
        => new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:Environments:Current", current)
            .WithSetting("ControlPlane:Environments:Links:0:Name", "dev")
            .WithSetting("ControlPlane:Environments:Links:0:Url", "https://sqlflow-dev.example.com")
            .WithSetting("ControlPlane:Environments:Links:0:Description", " Development ")
            .WithSetting("ControlPlane:Environments:Links:1:Name", "test")
            .WithSetting("ControlPlane:Environments:Links:1:Url", "https://sqlflow-test.example.com/")
            .WithSetting("ControlPlane:Environments:Links:2:Name", "prod")
            .WithSetting("ControlPlane:Environments:Links:2:Url", " https://sqlflow.example.com/workbench ")
            .WithSetting("ControlPlane:Environments:Links:2:Description", "Production");

    [Fact]
    public async Task TheConfiguredEnvironments_AreListedInTheirOrder_WithTheCurrentOneMarked()
    {
        await using var factory = Estate("TEST");
        using var client = factory.CreateClient();

        var environments = await GetAsync(client, await TokenAsync(client));

        Assert.Equal("test", environments.Current);
        EnvironmentDto[] expected =
        [
            new("dev", "Development", "https://sqlflow-dev.example.com", false),
            new("test", null, "https://sqlflow-test.example.com/", true),
            new("prod", "Production", "https://sqlflow.example.com/workbench", false),
        ];
        Assert.Equal(expected, environments.Environments);
    }

    [Fact]
    public async Task TheCurrentOneAlone_IsNamed_WithNoOtherToOpen()
    {
        await using var factory = new ControlPlaneAppFactory().WithSetting("ControlPlane:Environments:Current", "prod");
        using var client = factory.CreateClient();

        var environments = await GetAsync(client, await TokenAsync(client));

        Assert.Equal("prod", environments.Current);
        Assert.Empty(environments.Environments);
    }

    [Fact]
    public async Task NothingConfigured_NamesNoEnvironment()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        var environments = await GetAsync(client, await TokenAsync(client));

        Assert.Null(environments.Current);
        Assert.Empty(environments.Environments);
    }

    [Fact]
    public async Task TheList_IsNotShownToACallerWhoIsNotSignedIn()
    {
        await using var factory = Estate("dev");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/environments", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ACurrentOneTheListDoesNotGive_StartsTheHost_ShowsNoEnvironment_AndLogsWhy()
    {
        await using var factory = Estate("stage");
        using var client = factory.CreateClient();

        var environments = await GetAsync(client, await TokenAsync(client));

        Assert.Null(environments.Current);
        Assert.Empty(environments.Environments);
        Assert.Contains(factory.Logs, line => line.Contains("ControlPlane:Environments:Current is 'stage'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEntryWithABadAddress_StartsTheHost_LeavesThatEntryOut_AndLogsWhyWithoutTheAddress()
    {
        await using var factory = Estate("test")
            .WithSetting("ControlPlane:Environments:Links:2:Url", "https://operator:s3cret-Pa55@sqlflow.example.com");
        using var client = factory.CreateClient();

        var environments = await GetAsync(client, await TokenAsync(client));

        Assert.Equal("test", environments.Current);
        Assert.Equal(["dev", "test"], environments.Environments.Select(e => e.Name));
        Assert.Contains(factory.Logs, line => line.Contains("ControlPlane:Environments:Links:2:Url", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs, line => line.Contains("s3cret-Pa55", StringComparison.Ordinal));
    }

    private static async Task<EnvironmentsDto> GetAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/environments", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var environments = await response.Content.ReadFromJsonAsync<EnvironmentsDto>();
        Assert.NotNull(environments);
        return environments;
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
