using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The thin HTTP surface the protocols share: endpoint joining, resolved headers, auth application and the
/// request factories the executor retries from. A payload request factory re-opens the chunk stream per attempt,
/// which is what lets streaming and retry coexist (osdu/docs/reference/concepts/protocols.md, Retries while a payload
/// streams). Bodies are never buffered: a chunk streams from storage into the request with its known length, so a
/// landing zone that needs Content-Length gets it.
/// </summary>
public sealed class OsduHttpClient
{
    private readonly HttpRuntime _http;
    private readonly TargetAuth _auth;
    private readonly string _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public OsduHttpClient(HttpRuntime http, string endpoint, TargetAuth auth, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _auth = auth;
        _headers = headers;
    }

    public string Endpoint => _endpoint;

    /// <summary>A header the flow declares on every request (data-partition-id), or null when it does not.</summary>
    public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Every header the flow declares, resolved. The etp route puts them on its WebSocket upgrade, with <see cref="OsduCorrelation.Value"/>.</summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    public Uri Url(string pathTemplate, string? id = null, string? sessionId = null)
    {
        var path = pathTemplate
            .Replace("{id}", id is null ? string.Empty : UrlPath.EscapeSegment(id), StringComparison.Ordinal)
            .Replace("{sessionId}", sessionId is null ? string.Empty : UrlPath.EscapeSegment(sessionId), StringComparison.Ordinal);
        return Resolve(path);
    }

    /// <summary>A URL with arbitrary path tokens substituted ({workflow}, {runId}), escaped for a path segment.</summary>
    public Uri Url(string pathTemplate, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var path = pathTemplate;
        foreach (var (name, value) in tokens)
        {
            path = path.Replace("{" + name + "}", UrlPath.EscapeSegment(value), StringComparison.Ordinal);
        }

        return Resolve(path);
    }

    /// <summary>
    /// A resolved path template as a URL: under the flow's endpoint normally, or as it stands when the flow wrote
    /// an absolute one.
    ///
    /// A flow declares one endpoint, and for most protocols that endpoint is the OSDU platform root, where every
    /// service the protocol needs sits under its own <c>/api/&lt;service&gt;/</c> prefix. The wellbore DDMS is the
    /// exception: its paths (<c>/ddms/v3/...</c>, <c>/about</c>) are relative to the DDMS service itself, so a flow
    /// whose endpoint is the DDMS cannot reach the storage service by a path at all. Versions are owned by storage
    /// for every kind of record, so the history purge is exactly that case. Writing the whole URL is how such a
    /// flow says where the other service lives. Absolute URLs go through the same SSRF guard and allowlist as
    /// everything else, because every request does.
    /// </summary>
    private Uri Resolve(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute;
        }

        return new Uri(_endpoint + "/" + path.TrimStart('/'));
    }

    /// <summary>The URL with one query parameter added, keeping whatever the template already carries.</summary>
    public static Uri WithQuery(Uri url, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?');
        builder.Query = (query.Length == 0 ? string.Empty : query + "&") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        return builder.Uri;
    }

    /// <param name="method">The request method.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="body">The JSON body, or null for none.</param>
    /// <param name="allowStatuses">Non-2xx statuses to return instead of throwing.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <param name="idempotent">Whether the request may be repeated; null takes it from the method.</param>
    /// <param name="headers">Headers of this request alone, beside the flow's (a cache directive a read needs).</param>
    /// <param name="bareJsonType">
    /// Types the body <c>application/json</c> without its charset parameter, for a service that compares the header with
    /// the bare media type (RAFS refuses <c>application/json; charset=utf-8</c> on a record write, osdu/specs/rafs-ddms
    /// INTEGRATION.md section 1.3).
    /// </param>
    /// <param name="repeatRefused">
    /// Repeats a request that may not be repeated otherwise after a status saying the service refused it unread (408, 425,
    /// 429), as <see cref="HttpExecutor.SendAsync"/> describes.
    /// </param>
    /// <param name="mediaType">
    /// The body's media type when it is a JSON type other than <c>application/json</c>: <c>application/merge-patch+json</c>
    /// for storage's merge patch of a record (osdu/specs/core/storage/openapi.yaml, <c>PATCH /records/{id}</c>).
    /// </param>
    public Task<HttpFetchResult> SendJsonAsync(
        HttpMethod method, Uri url, JsonNode? body, IReadOnlySet<int>? allowStatuses, CancellationToken ct, bool? idempotent = null,
        IReadOnlyDictionary<string, string>? headers = null, bool bareJsonType = false, bool repeatRefused = false, string? mediaType = null)
        => SendJsonBytesAsync(method, url, body is null ? null : CanonicalJson.ToBytes(body), allowStatuses, ct, idempotent, headers, bareJsonType, repeatRefused, mediaType);

    /// <summary>
    /// <see cref="SendJsonAsync"/> with a body already serialised, sent as it stands: for a request the caller writes as a
    /// stream of values (the historian's points) rather than as one document. Null sends no body.
    /// </summary>
    public async Task<HttpFetchResult> SendJsonBytesAsync(
        HttpMethod method, Uri url, byte[]? body, IReadOnlySet<int>? allowStatuses, CancellationToken ct, bool? idempotent = null,
        IReadOnlyDictionary<string, string>? headers = null, bool bareJsonType = false, bool repeatRefused = false, string? mediaType = null)
    {
        return await WithFreshAuthAsync(auth => _http.Data.SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            Apply(request, auth);
            foreach (var (name, value) in headers ?? NoHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            request.Content = JsonBody(body, bareJsonType, mediaType);
            return request;
        }, allowStatuses, idempotent, repeatRefused, ct), ct).ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The JSON content of a request, including one that has nothing to send: zero bytes typed
    /// <c>application/json</c>.
    ///
    /// Several OSDU endpoints refuse a request that carries no <c>Content-Type</c> even when the operation takes no
    /// body: storage answers <c>415 "Content-Type 'null' is not supported"</c>, because its controllers declare
    /// <c>consumes</c> and Spring enforces that on bodiless siblings too. That covers the calls this system makes
    /// most carefully, the removals (<c>POST /records/{id}:delete</c>, <c>DELETE /records/{id}</c>), as well as the
    /// read-backs and status polls. Python clients never notice because their libraries send a bare header; .NET has
    /// nowhere to put a content header without content, so the request is given an empty one. The OSDU C# client
    /// does the same (<c>JsonContentTypeHandler</c>), and the platform's own REST scripts send the header on bodiless
    /// calls. An empty body is semantically no body. <paramref name="bare"/> leaves the charset parameter out of a body's type,
    /// and <paramref name="mediaType"/> names a JSON type other than <c>application/json</c>.
    /// </summary>
    internal static ByteArrayContent JsonBody(byte[]? bytes, bool bare = false, string? mediaType = null)
    {
        var content = new ByteArrayContent(bytes ?? []);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType ?? "application/json") { CharSet = bytes is null || bare ? null : "utf-8" };
        return content;
    }

    /// <summary>
    /// Runs a call under the resolved auth and, on a 401, drops the cached token and runs it exactly once more with
    /// a fresh one. A token is refreshed a minute before it expires, so a 401 means the assumption behind that
    /// cached token no longer holds (clock skew, an early revocation, a rotated client secret). Retrying once turns
    /// that into a hiccup instead of one failed attempt for every record that happened to be in flight; a 401 that
    /// survives the fresh token is a real authorisation failure and surfaces as one.
    /// </summary>
    private async Task<HttpFetchResult> WithFreshAuthAsync(Func<AppliedAuth, Task<HttpFetchResult>> send, CancellationToken ct)
    {
        var auth = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        try
        {
            return await send(auth).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode == 401)
        {
            _http.AuthResolver.Invalidate();
            var refreshed = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
            return await send(refreshed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streams an opaque payload to the service; <paramref name="open"/> is invoked per attempt so a retry re-opens
    /// the blob, and <paramref name="length"/> (when known) becomes the Content-Length so no chunked encoding is used.
    /// </summary>
    public async Task<HttpFetchResult> SendStreamAsync(HttpMethod method, Uri url, Func<Stream> open, string contentType, long? length, CancellationToken ct, bool? idempotent = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        return await WithFreshAuthAsync(auth => _http.Data.SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            Apply(request, auth);
            request.Content = StreamBody(open(), contentType, length);
            return request;
        }, null, idempotent, ct: ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a payload to a signed URL the service handed out (a landing zone): the URL carries its own
    /// authorisation, so the flow's auth and headers are not applied; only the upload headers the flow declares are.
    /// </summary>
    public Task<HttpFetchResult> SendToSignedUrlAsync(HttpMethod method, Uri signedUrl, Func<Stream> open, string contentType, long? length, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(headers);
        return _http.Data.SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, signedUrl);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            request.Content = StreamBody(open(), contentType, length);
            return request;
        }, null, ct: ct);
    }

    /// <summary>
    /// Sends a file as a multipart form to a location an object store signed with a POST policy: the policy's
    /// <paramref name="fields"/> in order, then the file, last, as the store requires. Like any signed location it carries
    /// its own authorisation, so neither the flow's auth nor its headers go with it. The file part states its length, so
    /// the form has one and is not sent chunked, which a POST policy upload refuses.
    /// </summary>
    public Task<HttpFetchResult> SendFormToSignedUrlAsync(
        Uri url, IReadOnlyList<KeyValuePair<string, string>> fields, string fileField, string fileName, Func<Stream> open, long length, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(open);
        return _http.Data.SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
            {
                form.Add(new StringContent(value), name);
            }

            form.Add(StreamBody(open(), contentType, length), fileField, fileName);
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }, null, ct: ct);
    }

    /// <summary>
    /// Sends a request the caller builds whole, to an object store a service handed out credentials for (a SAS, a scoped
    /// bearer token, a key triple the caller signs with): neither the flow's auth nor its headers go with it, and the
    /// reliability stack (rate limit, URL guard, retries, response cap) applies as to every request. <paramref name="build"/>
    /// runs for every attempt, so a signature is made afresh each time. <paramref name="idempotent"/> null takes it from
    /// the method.
    /// </summary>
    public Task<HttpFetchResult> SendAsIsAsync(Func<HttpRequestMessage> build, IReadOnlySet<int>? allowStatuses, bool? idempotent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        return _http.Data.SendAsync(build, allowStatuses, idempotent, ct: ct);
    }

    /// <summary>
    /// Reads a file from a signed URL the service handed out (a dataset's retrieval instructions): the URL carries its own
    /// authorisation, so neither the flow's auth nor its headers go with it, since some object stores refuse a request
    /// that also carries a bearer (osdu/specs/workflows/INTEGRATION.md section 2.2.1). The body is capped at the flow's
    /// response ceiling, as every read is.
    /// </summary>
    public async Task<byte[]> GetSignedUrlAsync(Uri signedUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signedUrl);
        var result = await _http.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, signedUrl), null, ct: ct).ConfigureAwait(false);
        return result.Body;
    }

    private static StreamContent StreamBody(Stream stream, string contentType, long? length)
    {
        var content = new StreamContent(stream, 1 << 16);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (length is { } known)
        {
            content.Headers.ContentLength = known;
        }

        return content;
    }

    public static JsonElement ParseJson(HttpFetchResult result, Uri url)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return JsonDocument.Parse(result.Body).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{HeaderRedaction.DescribeUrl(url)} returned a body that is not JSON: {Preview(result)}", ex);
        }
    }

    private static string Preview(HttpFetchResult result)
    {
        var text = Encoding.UTF8.GetString(result.Body);
        return HeaderRedaction.RedactMessage(text.Length <= 200 ? text : text[..200] + "...");
    }

    private void Apply(HttpRequestMessage request, AppliedAuth auth)
    {
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        auth.ApplyTo(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        OsduCorrelation.ApplyTo(request);
    }
}
