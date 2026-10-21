using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The fence around an AI assistant run, as the delivery module draws it (<see cref="DeliveryAssistantAccess"/>): the
/// endpoints the assistant's delivery tools call are open to the run's delegated token, and no other delivery endpoint
/// is. The routes each tool calls are read out of the MCP server's Rust sources, so a tool that starts reading a new route
/// fails here until the route is opened, and a route opened that no allowed tool calls fails here too.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryAssistantAccessTests
{
    private const string SomeGuid = "3f1c1b0e-8a53-4c1e-9d5e-6f0b7a2c9e41";

    private static readonly Regex ToolFunction = new(@"async\s+fn\s+(delivery_\w+)\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex BaseRoute = new(@"let\s+base\s*=\s*format!\(\s*""([^""]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex RouteLiteral = new(@"""((?:/api/v1|\{base\})[^""]*)""", RegexOptions.CultureInvariant);
    private static readonly Regex PostsIt = new(@"\.(send|post)\(\s*(?:&format!\(\s*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ReadsBase = new(@"\.(get|read|get_about)\(\s*&base\b", RegexOptions.CultureInvariant);
    private static readonly Regex Parameter = new(@"\{[^}]*\}", RegexOptions.CultureInvariant);

    /// <summary>One control-plane call a delivery tool makes: its method, its route with every parameter as <c>{}</c>.</summary>
    private sealed record ToolCall(string Tool, string Method, string Route);

    [Fact]
    public async Task EveryRouteAnAllowedTool_Calls_IsOpenToAnAssistantRun_ForTheMethodItCallsWith()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var endpoints = DeliveryEndpoints(factory);

        var allowed = DeliveryAssistantAccess.ReaderTools.ToHashSet(StringComparer.Ordinal);
        var calls = ToolCalls().Where(call => allowed.Contains(call.Tool)).ToList();
        Assert.True(calls.Count >= 40, $"Only found {calls.Count} calls the allowed delivery tools make; the parse is probably wrong.");

        foreach (var call in calls)
        {
            var endpoint = Resolve(endpoints, call);
            var access = endpoint.Metadata.GetMetadata<AssistantAccess>();
            Assert.True(access is not null, $"{call.Tool} calls {call.Method} {call.Route}, which is closed to an assistant run.");
            if (call.Method != HttpMethods.Get)
            {
                Assert.True(access.AllowsUnsafeMethods, $"{call.Tool} posts to {call.Route}, which an assistant run may only read.");
            }
        }
    }

    [Fact]
    public async Task NoDeliveryEndpoint_IsOpenToAnAssistantRun_ThatNoAllowedToolCalls()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var endpoints = DeliveryEndpoints(factory);

        var allowed = DeliveryAssistantAccess.ReaderTools.ToHashSet(StringComparer.Ordinal);
        var needed = ToolCalls().Where(call => allowed.Contains(call.Tool)).Select(call => Resolve(endpoints, call)).ToHashSet();
        var open = endpoints.Where(e => e.Metadata.GetMetadata<AssistantAccess>() is not null).ToList();

        var unneeded = open.Where(e => !needed.Contains(e)).Select(Describe).ToList();
        Assert.True(unneeded.Count == 0, "Open to an assistant run though no allowed delivery tool calls them: " + string.Join(", ", unneeded));

        // A write the assistant may make is one of the four previews, and nothing else.
        var writable = open.Where(e => e.Metadata.GetMetadata<AssistantAccess>()!.AllowsUnsafeMethods).Select(Name).Order(StringComparer.Ordinal);
        Assert.Equal(DeliveryAssistantAccess.Previews.Order(StringComparer.Ordinal), writable);
    }

    [Fact]
    public async Task EveryEndpointNamed_IsOneOfTheModules_AndNamedOnce()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var names = DeliveryEndpoints(factory).Select(Name).ToHashSet(StringComparer.Ordinal);

        var phantom = DeliveryAssistantAccess.Reads.Concat(DeliveryAssistantAccess.Previews).Where(name => !names.Contains(name)).ToList();
        Assert.True(phantom.Count == 0, "Named as open to an assistant run but no delivery endpoint has the name: " + string.Join(", ", phantom));
        Assert.Empty(DeliveryAssistantAccess.Reads.Intersect(DeliveryAssistantAccess.Previews, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheToolsKeptFromTheAssistant_CallNothingItMayWriteTo()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var endpoints = DeliveryEndpoints(factory);

        var excluded = DeliveryAssistantAccess.ActionTools.ToHashSet(StringComparer.Ordinal);
        var calls = ToolCalls().Where(call => excluded.Contains(call.Tool)).ToList();
        Assert.True(calls.Count >= excluded.Count, "Every excluded tool calls the control plane at least once; the parse is probably wrong.");

        // What an operator action posts is its action; reading what it reports on may be shared with an allowed tool.
        foreach (var call in calls.Where(call => call.Method != HttpMethods.Get))
        {
            var access = Resolve(endpoints, call).Metadata.GetMetadata<AssistantAccess>();
            Assert.True(access is null, $"{call.Tool} is an operator action, and {call.Method} {call.Route} is open to an assistant run.");
        }
    }

    [Theory]
    [InlineData("GET", "/api/v1/delivery/partitions")]
    [InlineData("GET", "/api/v1/delivery/records?q=abc")]
    [InlineData("GET", "/api/v1/delivery/flows/" + SomeGuid + "/stats")]
    [InlineData("POST", "/api/v1/delivery/mapping-builder/coverage")]
    public async Task ADelegatedToken_PassesTheFence_OnTheRoutesTheDeliveryToolsCall(string method, string path)
    {
        // Past the fence the endpoint does its own work, and against a host with no module database may fail; this asserts
        // only that neither authentication nor the fence stopped the call.
        await using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, DelegatedToken(factory), method, path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(AssistantDelegationMiddleware.DeniedTitle, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    // Operator actions: the tools that make them are kept from the assistant.
    [InlineData("POST", "/api/v1/delivery/flows/" + SomeGuid + "/release")]
    [InlineData("POST", "/api/v1/delivery/flows/" + SomeGuid + "/probe")]
    [InlineData("POST", "/api/v1/delivery/records/" + SomeGuid + "/" + SomeGuid + "/redeliver")]
    [InlineData("POST", "/api/v1/delivery/cache/tags/decide")]
    // Data no tool reads: a dimension's values and table, a record's source row, its rendered document, what OSDU
    // holds for it, and its stored artifacts.
    [InlineData("GET", "/api/v1/delivery/dimensions/1/values")]
    [InlineData("GET", "/api/v1/delivery/dimensions/1/table")]
    [InlineData("POST", "/api/v1/delivery/records/" + SomeGuid + "/" + SomeGuid + "/source")]
    [InlineData("POST", "/api/v1/delivery/records/" + SomeGuid + "/" + SomeGuid + "/preview")]
    [InlineData("POST", "/api/v1/delivery/records/" + SomeGuid + "/" + SomeGuid + "/read")]
    [InlineData("GET", "/api/v1/delivery/records/" + SomeGuid + "/" + SomeGuid + "/artifacts")]
    // A read route opened to the assistant does not open the operate route beside it, which any signed-in person may take
    // (syncing the OSDU data definitions).
    [InlineData("POST", "/api/v1/delivery/templates/osdu/sync")]
    public async Task ADelegatedToken_IsRefused_OnEveryOtherDeliveryRoute(string method, string path)
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, DelegatedToken(factory), method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AssistantDelegationMiddleware.DeniedTitle, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersonsOwnSession_IsNotFenced_OnARouteClosedToTheAssistant()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var nowUtc = DateTime.UtcNow;
        var session = factory.Services.GetRequiredService<TokenIssuer>()
            .Issue("chat-user", ["read", "operate"], nowUtc, "Operator", Guid.NewGuid(), nowUtc).Token;

        using var response = await SendAsync(client, session, "GET", "/api/v1/delivery/dimensions/1/values");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(AssistantDelegationMiddleware.DeniedTitle, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDelegatedTokenTheseTestsUse_IsOne()
    {
        // The refusals above are only meaningful if the token is the assistant's: one that carries the marker.
        await using var factory = Host();
        using var client = factory.CreateClient();
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(DelegatedToken(factory));
        Assert.Equal(AssistantDelegation.ClaimValue, jwt.GetClaim(AssistantDelegation.ClaimType).Value);
    }

    /// <summary>The calls every delivery tool makes, read out of the MCP server's sources.</summary>
    private static List<ToolCall> ToolCalls()
    {
        var folder = RepositoryRoot.Combine("osdu", "hosts", "osdu-delivery-mcp", "src", "tools");
        Assert.True(Directory.Exists(folder), $"Expected the delivery tools' sources at {folder}.");
        var calls = new List<ToolCall>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.rs").Order(StringComparer.Ordinal))
        {
            var source = File.ReadAllText(file);
            var starts = ToolFunction.Matches(source).ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Index : source.Length;
                calls.AddRange(CallsOf(starts[i].Groups[1].Value, source[starts[i].Index..end]));
            }
        }

        return calls.Distinct().ToList();
    }

    /// <summary>
    /// The calls one tool's body makes. Every route literal in a tool's body is a route it calls, by GET unless the
    /// literal is what it posts; a <c>base</c> route counts as a call only when the body reads it itself, and the routes
    /// composed on it are read from it.
    /// </summary>
    private static IEnumerable<ToolCall> CallsOf(string tool, string body)
    {
        var baseRoute = BaseRoute.Match(body);
        foreach (Match literal in RouteLiteral.Matches(body))
        {
            if (baseRoute.Success && literal.Groups[1].Index == baseRoute.Groups[1].Index)
            {
                continue;
            }

            var route = baseRoute.Success ? literal.Groups[1].Value.Replace("{base}", baseRoute.Groups[1].Value, StringComparison.Ordinal) : literal.Groups[1].Value;
            var before = body[Math.Max(0, literal.Index - 40)..literal.Index];
            var method = PostsIt.IsMatch(before) ? HttpMethods.Post : HttpMethods.Get;
            yield return new ToolCall(tool, method, Normalize(route));
        }

        if (baseRoute.Success && ReadsBase.IsMatch(body))
        {
            yield return new ToolCall(tool, HttpMethods.Get, Normalize(baseRoute.Groups[1].Value));
        }
    }

    /// <summary>A route with its query dropped and every parameter written <c>{}</c>, whatever it is named or constrained to.</summary>
    private static string Normalize(string route)
    {
        var path = route.Split('?')[0].TrimEnd('/');
        return Parameter.Replace(path.StartsWith('/') ? path : "/" + path, "{}");
    }

    /// <summary>The delivery module's endpoints, as the built control plane routes them.</summary>
    private static List<RouteEndpoint> DeliveryEndpoints(ControlPlaneAppFactory factory)
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => Normalize(e.RoutePattern.RawText ?? string.Empty).StartsWith("/api/v1/delivery/", StringComparison.Ordinal))
            .ToList();
        Assert.True(endpoints.Count > 100, $"Only {endpoints.Count} delivery endpoints were mapped; the module is probably not composed.");
        return endpoints;
    }

    /// <summary>The one endpoint that answers <paramref name="call"/>.</summary>
    private static RouteEndpoint Resolve(IReadOnlyList<RouteEndpoint> endpoints, ToolCall call)
    {
        var matches = endpoints
            .Where(e => Normalize(e.RoutePattern.RawText ?? string.Empty) == call.Route)
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(call.Method, StringComparer.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.True(matches.Count == 1, $"{call.Tool} calls {call.Method} {call.Route}, which {matches.Count} delivery endpoints answer.");
        return matches[0];
    }

    private static string Name(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.RoutePattern.RawText ?? string.Empty;

    private static string Describe(RouteEndpoint endpoint)
        => $"{Name(endpoint)} ({string.Join("|", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText})";

    private static string DelegatedToken(ControlPlaneAppFactory factory)
        => factory.Services.GetRequiredService<TokenIssuer>().IssueAssistantDelegation(
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "chat-user"),
                new Claim("uid", Guid.NewGuid().ToString("D")),
                new Claim("role", "Operator"),
                new Claim("scope", "read operate"),
            ], "test")),
            TimeSpan.FromMinutes(4),
            DateTime.UtcNow).Token;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method is "POST" or "PUT" or "DELETE")
        {
            request.Content = JsonContent.Create(new { });
        }

        return await client.SendAsync(request);
    }

    /// <summary>The control plane with the module, and without the settings a test host must not act on (no network warm-up).</summary>
    private static ControlPlaneAppFactory Host()
        => new ControlPlaneAppFactory()
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
}
