using System.Globalization;
using System.Net;
using System.Text.Json;
using SqlFlow.ControlPlane.Infrastructure;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The control plane's rate limit as a browser meets it: past the window's permits a call is answered 429 with a
/// <c>Retry-After</c> that says how long the window has left, a problem body saying the same, and the CORS headers a
/// cross-origin page needs to read both; the health probes stay outside the limit.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RateLimitHostTests
{
    private const string GuiOrigin = "http://localhost:5173";

    private static ControlPlaneAppFactory Host() => new ControlPlaneAppFactory()
        .WithSetting("ControlPlane:RateLimit:PermitPerWindow", "3")
        .WithSetting("ControlPlane:RateLimit:WindowSeconds", "60")
        .WithSetting("ControlPlane:Cors:AllowedOrigins:0", GuiOrigin)
        .WithSetting("ControlPlane:Worker:Enabled", "false");

    private static async Task<HttpResponseMessage> ProvidersAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/auth/providers", UriKind.Relative));
        request.Headers.Add("Origin", GuiOrigin);
        request.Headers.Add("Accept", "application/json");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_call_past_the_windows_permits_is_told_how_long_until_calls_are_taken_again()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        for (var call = 0; call < 3; call++)
        {
            using var taken = await ProvidersAsync(client);
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        }

        using var refused = await ProvidersAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var retryAfter = int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After")), NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);

        // The page that made the call reads the header and the correlation id: CORS lets it, on the refusal too.
        Assert.Equal(GuiOrigin, Assert.Single(refused.Headers.GetValues("Access-Control-Allow-Origin")));
        var exposed = refused.Headers.GetValues("Access-Control-Expose-Headers")
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.Contains("Retry-After", exposed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(CorrelationIdMiddleware.HeaderName, exposed, StringComparer.OrdinalIgnoreCase);

        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(RateLimitRejection.Title, problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"again in {retryAfter} s"), problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            Assert.Single(refused.Headers.GetValues(CorrelationIdMiddleware.HeaderName)),
            problem.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task The_health_probes_answer_whatever_the_caller_has_spent()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        for (var call = 0; call < 4; call++)
        {
            using var _ = await ProvidersAsync(client);
        }

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
