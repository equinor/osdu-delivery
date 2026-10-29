using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Workflows;

/// <summary>
/// Reads one XCom entry of a workflow run: what a DAG task pushed, which the Workflow contract never returns
/// (osdu/specs/workflows/INTEGRATION.md section 5.2.1). The Workflow service runs a workflow as the Airflow DAG run whose id
/// is the workflow run id (project 146 at <c>1a3e3fa35ebed1093c3498e21c3827a9caf1e01c</c>,
/// <c>BaseAirflowWorkflowEngineService.java:49-50</c>), so the run id names the DAG run.
/// </summary>
public interface IWorkflowXCom
{
    /// <summary>The entry's value, or null when the task pushed nothing under the key.</summary>
    Task<JsonNode?> ReadAsync(string workflow, string runId, string task, string key, CancellationToken ct);
}

/// <summary>
/// The XCom entries of a run's latest task, as the Workflow service returns them (openapi workflow v1,
/// <c>GET /v1/workflow/{workflow_name}/workflowRun/{runId}/latestInfo</c>, typed only as an object). On an Airflow 2 or 3
/// engine the service reads the task instance with the latest end date and returns its entries under <c>xcom</c>, each
/// as the text Airflow gives (project 146, <c>BaseAirflowWorkflowEngineExtension.java:84-109, 170-191</c>). Only the
/// latest task's entries are there, so an entry of another task is refused rather than guessed at.
/// </summary>
public sealed class LatestInfoXCom : IWorkflowXCom
{
    public const string DefaultPath = "/api/workflow/v1/workflow/{workflow}/workflowRun/{runId}/latestInfo";

    private readonly OsduHttpClient _client;
    private readonly string _path;

    public LatestInfoXCom(OsduHttpClient client, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _path = path ?? DefaultPath;
    }

    public async Task<JsonNode?> ReadAsync(string workflow, string runId, string task, string key, CancellationToken ct)
    {
        var url = _client.Url(_path, new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = workflow, ["runId"] = runId });
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            throw new DeliveryException(
                $"the Workflow service answers no run details for run {runId} of {workflow} (latestInfo is served only on an Airflow 2 or 3 engine); declare target.airflow to read the task's XCom entry from Airflow");
        }

        if (JsonNode.Parse(result.Body) is not JsonObject root)
        {
            throw new DeliveryException($"{url.AbsolutePath} answered with something other than a JSON object.");
        }

        var latest = root["task_id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null;
        if (!string.Equals(latest, task, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"the Workflow service returns the XCom entries of run {runId}'s latest task, {latest ?? "none"}, and the route reads {task}; declare target.airflow to read that task's entry from Airflow");
        }

        return root["xcom"] is JsonObject xcom && xcom.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;
    }
}

/// <summary>
/// An XCom entry read from the Airflow REST API behind the Workflow service: Airflow 2
/// (<c>GET /api/v1/dags/{dag_id}/dagRuns/{dag_run_id}/taskInstances/{task_id}/xcomEntries/{xcom_key}</c>, basic
/// authentication, the value as text; osdu/specs/workflows/airflow/v1.yaml) or Airflow 3 (the same path under
/// <c>/api/v2</c>, a bearer token from <c>POST /auth/token</c>, the value as JSON;
/// osdu/specs/workflows/airflow/v2-rest-api-generated.yaml and v2-simple-auth-manager-generated.yaml).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable", Justification = "The SemaphoreSlim wait handle is never accessed; the reader lives as long as the protocol that owns it.")]
public sealed class AirflowXCom : IWorkflowXCom
{
    public const string TokenPath = "/auth/token";

    private readonly HttpRuntime _http;
    private readonly AirflowAccess _access;
    private readonly ISecretResolver _secrets;
    private readonly TimeProvider _time;
    private readonly AuthResolver _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _endpoint;
    private Dictionary<string, string>? _headers;
    private string? _token;
    private DateTimeOffset _tokenUntil;

    public AirflowXCom(HttpRuntime http, AirflowAccess access, ISecretResolver secrets, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(secrets);
        _http = http;
        _access = access;
        _secrets = secrets;
        _time = time ?? TimeProvider.System;
        _auth = new AuthResolver(secrets, _time);
    }

    /// <summary>The path of one XCom entry under the Airflow endpoint, for the API version the flow declares.</summary>
    public static string EntryPath(AirflowApiVersion version, string dag, string runId, string task, string key)
        => $"/api/{(version == AirflowApiVersion.V2 ? "v2" : "v1")}/dags/{UrlPath.EscapeSegment(dag)}/dagRuns/{UrlPath.EscapeSegment(runId)}/taskInstances/{UrlPath.EscapeSegment(task)}/xcomEntries/{UrlPath.EscapeSegment(key)}";

    public async Task<JsonNode?> ReadAsync(string workflow, string runId, string task, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        var (endpoint, headers) = await ResolveAsync(ct).ConfigureAwait(false);
        var url = new Uri(endpoint + EntryPath(_access.ApiVersion, workflow, runId, task, key));
        var authorization = await AuthorizationAsync(endpoint, ct).ConfigureAwait(false);
        var result = await _http.Data.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            authorization.ApplyTo(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            OsduCorrelation.ApplyTo(request);
            return request;
        }, new HashSet<int> { 404 }, idempotent: true, ct: ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return null;
        }

        if (JsonNode.Parse(result.Body) is not JsonObject entry)
        {
            throw new DeliveryException($"Airflow answered the XCom entry {key} of task {task} with something other than a JSON object.");
        }

        return entry["value"]?.DeepClone();
    }

    private async Task<(string Endpoint, Dictionary<string, string> Headers)> ResolveAsync(CancellationToken ct)
    {
        if (_endpoint is not null && _headers is not null)
        {
            return (_endpoint, _headers);
        }

        var endpoint = (await _secrets.ResolveAsync(_access.Endpoint, ct).ConfigureAwait(false)).TrimEnd('/');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in _access.Headers)
        {
            headers[name] = await _secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        _endpoint = endpoint;
        _headers = headers;
        return (endpoint, headers);
    }

    /// <summary>
    /// What authorises a read: the flow's auth as it stands on Airflow 2; on Airflow 3 a token the auth manager issues for
    /// the user and password the flow's basic auth names, kept until shortly before it expires.
    /// </summary>
    private async Task<AppliedAuth> AuthorizationAsync(string endpoint, CancellationToken ct)
    {
        if (_access.ApiVersion != AirflowApiVersion.V2 || _access.Auth.Type != TargetAuthType.Basic)
        {
            return await _auth.ResolveAsync(_access.Auth, _http.Auth, ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is null || _time.GetUtcNow() >= _tokenUntil)
            {
                (_token, _tokenUntil) = await TokenAsync(endpoint, ct).ConfigureAwait(false);
            }

            return AppliedAuth.Header("Authorization", "Bearer " + _token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A token from Airflow 3's auth manager (<c>POST /auth/token</c> with <c>{username, password}</c>, answering
    /// <c>{access_token}</c>; osdu/specs/workflows/airflow/v2-simple-auth-manager-generated.yaml), kept until five minutes
    /// before the expiry its payload states, as the Workflow service keeps its own (project 146,
    /// <c>Airflow3TokenClient.java:58-71, 146-192</c>), or for half an hour when it states none.
    /// </summary>
    private async Task<(string Token, DateTimeOffset Until)> TokenAsync(string endpoint, CancellationToken ct)
    {
        var user = await _secrets.ResolveAsync(_access.Auth.SecondarySecretRef ?? throw new DeliveryException("target.airflow.auth needs secondarySecretRef (the Airflow user) to ask Airflow 3 for a token."), ct).ConfigureAwait(false);
        var password = await _secrets.ResolveAsync(_access.Auth.SecretRef ?? throw new DeliveryException("target.airflow.auth needs secretRef (the Airflow password) to ask Airflow 3 for a token."), ct).ConfigureAwait(false);
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["username"] = user, ["password"] = password }.ToJsonString());
        var url = new Uri(endpoint + TokenPath);
        var result = await _http.Auth.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = OsduHttpClient.JsonBody(body);
            return request;
        }, ct: ct, idempotent: true).ConfigureAwait(false);

        string? token;
        try
        {
            token = JsonNode.Parse(result.Body)?["access_token"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new DeliveryException($"Airflow's token endpoint {url.AbsolutePath} did not answer with an access token.", ex);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new DeliveryException($"Airflow's token endpoint {url.AbsolutePath} did not answer with an access token.");
        }

        var now = _time.GetUtcNow();
        var until = ExpiryOf(token) is { } expiry && expiry - TimeSpan.FromMinutes(5) > now
            ? expiry - TimeSpan.FromMinutes(5)
            : now + TimeSpan.FromMinutes(30);
        return (token, until);
    }

    /// <summary>The <c>exp</c> claim of a JWT, read without validating it (Airflow validates its own tokens), or null.</summary>
    internal static DateTimeOffset? ExpiryOf(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            var claims = JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
            return claims?["exp"] is JsonValue exp && exp.TryGetValue<long>(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>
/// The OSDU record ids in a value a workflow produced: a list of ids, a JSON document holding them, or the text Airflow
/// gives for a Python value (<c>{'energyml_manifest_creation': ['dev:dataset--File.Generic:abc:']}</c>). Each id is
/// taken without its version, as the upstream collections strip a trailing colon before reading a record
/// (osdu/specs/workflows/INTEGRATION.md sections 3.6 and 4).
/// </summary>
public static partial class WorkflowIds
{
    public static IReadOnlyList<string> Extract(JsonNode? value, string? entityType)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in Strings(value))
        {
            foreach (Match match in RecordId().Matches(text))
            {
                var id = TargetId.WithoutVersion(match.Value);
                if (entityType is not null && !string.Equals(match.Groups["type"].Value, entityType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(id))
                {
                    found.Add(id);
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    foreach (var text in Strings(child))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var text in Strings(child))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                break;
        }
    }

    /// <summary>An OSDU R3 record id or reference: a partition, an entity type with its group, and a unique segment (openapi storage v2, Record.id).</summary>
    [GeneratedRegex(@"(?<![\w\-\.:])[\w\-\.]+:(?<type>[\w\-\.]+--[\w\-\.]+):[\w\-\.\%][\w\-\.\:\%]*", RegexOptions.CultureInvariant)]
    private static partial Regex RecordId();
}
