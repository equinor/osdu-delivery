using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// Tests a flow's OSDU client the way a delivery uses it: the client-credentials exchange a run makes (the form a flow's
/// token endpoint receives: grant_type, client_id, client_secret, scope), then one read, the legal tag's
/// (<c>GET /api/legal/v1/legaltags/{name}</c> with the partition's <c>data-partition-id</c>, as osdu/specs/core/legal pins
/// it). Nothing is written to OSDU. The token never leaves this class, and what a service answers is reduced to its status
/// and the reason it gave.
/// </summary>
internal sealed class OsduSignIn : IOsduSignIn
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The correlation id the read carries, as every OSDU request does (osdu/docs/reference/concepts/request-correlation.md):
    /// the product's own operation <c>signin</c> with a new id. This project keeps to SQLFlow's core, so it writes the value
    /// the module's <c>OsduCorrelation</c> composes itself, and a test holds the two to one form.
    /// </summary>
    internal const string CorrelationHeader = "correlation-id";

    internal static string CorrelationValue() => $"osdu-delivery-signin-{Guid.NewGuid():D}";

    public async Task<OsduSignInResult> TestAsync(
        Uri tokenUrl, string clientId, string clientSecret, string scope, Uri? platform, string? partition, string? legalTag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokenUrl);
        using var http = new HttpClient { Timeout = Timeout };
        string? token;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = scope,
            });
            using var response = await http.PostAsync(tokenUrl, form, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new OsduSignInResult(tokenUrl.Host, false, (int)response.StatusCode, Reason(body, response.ReasonPhrase), null, null);
            }

            token = Field(body, "access_token");
            if (string.IsNullOrEmpty(token))
            {
                return new OsduSignInResult(tokenUrl.Host, false, (int)response.StatusCode, "the token endpoint answered without an access_token", null, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new OsduSignInResult(tokenUrl.Host, false, 0, FirstLine(SecretHygiene.RedactedMessage(ex)), null, null);
        }

        if (platform is null || string.IsNullOrEmpty(partition) || string.IsNullOrEmpty(legalTag))
        {
            return new OsduSignInResult(tokenUrl.Host, true, 200, null, null, null);
        }

        try
        {
            var uri = new Uri(platform, $"/api/legal/v1/legaltags/{Uri.EscapeDataString(legalTag)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("data-partition-id", partition);
            request.Headers.TryAddWithoutValidation(CorrelationHeader, CorrelationValue());
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new OsduSignInResult(
                tokenUrl.Host, true, 200, null, (int)response.StatusCode,
                response.StatusCode == HttpStatusCode.OK ? null : Reason(body, response.ReasonPhrase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new OsduSignInResult(tokenUrl.Host, true, 200, null, 0, FirstLine(SecretHygiene.RedactedMessage(ex)));
        }
    }

    /// <summary>What a service said went wrong: its error_description, message, error or reason, else its status phrase.</summary>
    internal static string Reason(string body, string? phrase)
    {
        foreach (var name in new[] { "error_description", "message", "error", "reason" })
        {
            if (Field(body, name) is { Length: > 0 } said)
            {
                return FirstLine(said);
            }
        }

        return string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('<') ? phrase ?? "no reason given" : FirstLine(body);
    }

    private static string? Field(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end]).Trim();
        return line.Length > 240 ? line[..237] + "..." : line;
    }
}
