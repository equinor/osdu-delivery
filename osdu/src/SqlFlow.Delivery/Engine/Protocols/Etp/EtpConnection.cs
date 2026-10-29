using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// Opens the ETP sessions the route works in: the flow's endpoint as a WebSocket URL, the flow's headers, and its
/// credentials resolved afresh for every session, so a long run never carries an expired token into an upgrade
/// (osdu/specs/reservoir-ddms/INTEGRATION.md sections 1.3 and 3.1). Every session opens through the flow's own HTTP
/// stack, so the address policy, the TLS setting and the connect timeout are the ones its other calls use.
/// </summary>
public sealed class EtpConnection
{
    private readonly HttpRuntime _http;
    private readonly TargetAuth _auth;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly UrlGuard _guard;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public EtpConnection(
        HttpRuntime http,
        Uri endpoint,
        TargetAuth auth,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyList<string> allowlist,
        EtpSessionOptions options,
        ILogger log,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _http = http;
        _auth = auth;
        _headers = headers;
        _guard = new UrlGuard(allowlist, http.Network);
        _log = log;
        _time = time ?? TimeProvider.System;
        Endpoint = endpoint;
        Options = options;
    }

    /// <summary>Where the ETP endpoint answers, as a WebSocket URL.</summary>
    public Uri Endpoint { get; }

    /// <summary>What every session of this connection announces and how long it waits.</summary>
    public EtpSessionOptions Options { get; }

    /// <summary>
    /// The WebSocket URL of an ETP endpoint under an OSDU host: the flow's endpoint with the ETP path, as
    /// <c>wss</c> where the flow is <c>https</c> (section 1.1).
    /// </summary>
    public static Uri WebSocketUri(string endpoint, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute, out var url))
        {
            throw new FlowValidationException($"The target endpoint '{HeaderRedaction.DescribeUrl(endpoint)}' with the ETP path '{path}' is not a URL.");
        }

        return new UriBuilder(url)
        {
            Scheme = url.Scheme switch
            {
                "https" or "wss" => "wss",
                "http" or "ws" => "ws",
                _ => throw new FlowValidationException($"The target endpoint '{HeaderRedaction.DescribeUrl(endpoint)}' is not an http or https URL, so it has no ETP WebSocket."),
            },
        }.Uri;
    }

    /// <summary>
    /// Opens one session, with the flow's credentials resolved into the upgrade and the correlation id of the run or operation
    /// opening it, as every OSDU request carries one.
    /// </summary>
    public async Task<EtpSession> OpenAsync(CancellationToken ct = default)
    {
        var applied = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in applied.Headers)
        {
            headers[name] = value;
        }

        headers[OsduCorrelation.HeaderName] = OsduCorrelation.Value;

        var endpoint = Endpoint;
        if (applied.Query.Count > 0)
        {
            var query = string.Join("&", applied.Query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
            endpoint = new UriBuilder(Endpoint) { Query = string.IsNullOrEmpty(Endpoint.Query) ? query : Endpoint.Query.TrimStart('?') + "&" + query }.Uri;
        }

        return await EtpSession.OpenAsync(endpoint, headers, _http.Invoker, _guard, Options, _log, _time, ct).ConfigureAwait(false);
    }
}
