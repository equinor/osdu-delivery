using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Refuses a literal secret in the module's flow documents: a credential in a document under source control is a defect,
/// so every key that carries one holds a reference the node resolves where it runs (<c>${env:NAME}</c> or
/// <c>${keyvault:vault/secret}</c>), never the value. The keys, by how the code uses them:
/// <list type="bullet">
/// <item>an auth block's <c>secretRef</c> (a delivery flow's <c>target.auth</c> and <c>target.airflow.auth</c>, every other
/// kind's <c>source.auth</c>): the bearer token, the API key, the basic password or the client secret, whatever the scheme.
/// Its <c>secondarySecretRef</c> is the basic user name or the OAuth client id, an identifier, so a literal is allowed;</item>
/// <item>a field of an auth block's token request named as a secret (<c>client_secret</c>, <c>password</c>,
/// <c>refresh_token</c>, <c>client_assertion</c>; <see cref="HeaderRedaction.IsSecretName"/> decides): a
/// <c>client_id</c>, <c>scope</c> or <c>audience</c> is not one;</item>
/// <item>a request header that carries a credential (<see cref="HeaderRedaction.IsSensitive"/>: Authorization, an API or
/// subscription key, a token), which may name its scheme before the reference (<c>Bearer ${env:NAME}</c>);</item>
/// <item>an endpoint or token URL written literally whose user info or query carries a credential.</item>
/// </list>
/// The upload headers sent to a signed URL and the entries of a workflow run's payload are sent as written, never
/// resolved, so a reference cannot stand there and a credential there is refused outright. Every message names the key and
/// the fix, never the value. The connection a flow reads its ingestion tables over is checked where it is mapped
/// (<see cref="SqlFlow.Delivery.Source.IngestionConnection.CheckDeclared"/>); a workflow route's secrets, and the credentials
/// its stages' contexts name, where the workflow is (the workflow mapper and
/// <see cref="Engine.Workflows.WorkflowContextCheck"/>, against the contract of the workflow each stage runs).
/// </summary>
internal static partial class DocumentSecrets
{
    private const string Fix = "A flow document holds references only: write ${keyvault:vault/secret} or ${env:NAME}, and keep the value in the key vault or the environment of the nodes that run the flow (locally, the git-ignored .sqlflow/env file).";

    /// <summary>A delivery flow: its target's endpoint, auth and headers, the Airflow behind it, and what its protocol options send as written.</summary>
    public static void Check(FlowYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        if (y.Target is { } target)
        {
            Url(target.Endpoint, "target.endpoint", source);
            Auth(target.Auth, "target.auth", source);
            Headers(target.Headers, "target.headers", source);
            if (target.Airflow is { } airflow)
            {
                Url(airflow.Endpoint, "target.airflow.endpoint", source);
                Auth(airflow.Auth, "target.airflow.auth", source);
                Headers(airflow.Headers, "target.airflow.headers", source);
            }

            SentAsWritten(target.ProtocolOptions, "target.protocolOptions", source);
        }

        if (y.Interfaces is not { } interfaces)
        {
            return;
        }

        foreach (var (name, declared) in interfaces)
        {
            SentAsWritten(declared?.ProtocolOptions, $"interfaces.{name}.protocolOptions", source);
        }
    }

    public static void Check(RetrievalYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        OsduSource(y.Source?.Endpoint, y.Source?.Auth, y.Source?.Headers, source);
    }

    public static void Check(CacheYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        OsduSource(y.Source?.Endpoint, y.Source?.Auth, y.Source?.Headers, source);
    }

    public static void Check(AssertionYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        OsduSource(y.Source?.Endpoint, y.Source?.Auth, y.Source?.Headers, source);
    }

    public static void Check(DimensionFlowYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        OsduSource(y.Source?.Endpoint, y.Source?.Auth, y.Source?.Headers, source);
    }

    public static void Check(InventoryFlowYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        OsduSource(y.Source?.Endpoint, y.Source?.Auth, y.Source?.Headers, source);
    }

    /// <summary>
    /// A removal flow: a reader source's location written literally with a credential in it. A table source's connection is
    /// checked where it is mapped, as a delivery flow's is.
    /// </summary>
    public static void Check(RemovalFlowYaml y, string source)
    {
        ArgumentNullException.ThrowIfNull(y);
        for (var i = 0; i < (y.Removals?.Count ?? 0); i++)
        {
            var entry = y.Removals![i];
            Url(entry?.Keys?.Location, $"removals[{i}].keys.location", source);
            Url(entry?.Removed?.Location, $"removals[{i}].removed.location", source);
        }
    }

    /// <summary>The OSDU source every kind but delivery reads: its endpoint, auth and headers.</summary>
    private static void OsduSource(string? endpoint, TargetAuthYaml? auth, Dictionary<string, string>? headers, string source)
    {
        Url(endpoint, "source.endpoint", source);
        Auth(auth, "source.auth", source);
        Headers(headers, "source.headers", source);
    }

    private static void Auth(TargetAuthYaml? auth, string key, string source)
    {
        if (auth is null)
        {
            return;
        }

        if (IsLiteral(auth.SecretRef, schemeAllowed: false))
        {
            throw new FlowValidationException(
                $"{source}: {key}.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). {Fix}");
        }

        if (auth.Token is not { } token)
        {
            return;
        }

        Url(token.Url, key + ".token.url", source);
        Url(token.DiscoveryUrl, key + ".token.discoveryUrl", source);
        foreach (var (name, value) in token.Body ?? [])
        {
            if (HeaderRedaction.IsSecretName(name) && IsLiteral(value, schemeAllowed: false))
            {
                throw new FlowValidationException(
                    $"{source}: {key}.token.body.{name} holds a literal value, and a field of the token request named as a secret is a credential. {Fix}");
            }
        }
    }

    private static void Headers(Dictionary<string, string>? headers, string key, string source)
    {
        foreach (var (name, value) in headers ?? [])
        {
            if (HeaderRedaction.IsSensitive(name) && IsLiteral(value, schemeAllowed: true))
            {
                throw new FlowValidationException(
                    $"{source}: {key}.{name} holds a literal value, and the header carries a credential; it holds a reference, after its scheme when it has one (Bearer ${{env:NAME}}). {Fix}");
            }
        }
    }

    /// <summary>What the protocol options send as written: the upload headers and the workflow run's payload.</summary>
    private static void SentAsWritten(ProtocolOptionsYaml? options, string key, string source)
    {
        if (options is null)
        {
            return;
        }

        if (options.UploadHeaders?.Keys.FirstOrDefault(HeaderRedaction.IsSensitive) is { } header)
        {
            throw new FlowValidationException(
                $"{source}: {key}.uploadHeaders.{header} carries a credential, and the upload headers go to the signed URL as written, never resolved. "
                + "The signed URL carries its own credential: remove the header.");
        }

        if (options.WorkflowPayload?.Keys.FirstOrDefault(HeaderRedaction.IsSecretName) is { } entry)
        {
            throw new FlowValidationException(
                $"{source}: {key}.workflowPayload.{entry} is named as a secret, and the workflow payload goes as written into a run the Workflow service keeps. "
                + "The service authenticates the run with the flow's own auth: remove the entry.");
        }
    }

    /// <summary>A URL written literally whose user info or query carries a credential; what a reference stands for is the node's to resolve.</summary>
    private static void Url(string? value, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (HeaderRedaction.QuotesCredential(Reference().Replace(value, string.Empty)))
        {
            throw new FlowValidationException(
                $"{source}: {key} carries a credential in its user info or query string; put the URL, or the credential in it, behind a reference. {Fix}");
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> holds something other than references: text beside them, or nothing but text.
    /// <paramref name="schemeAllowed"/> lets one word stand before them, an Authorization header's scheme. Empty is no value.
    /// </summary>
    private static bool IsLiteral(string? value, bool schemeAllowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Reference().IsMatch(value))
        {
            return true;
        }

        var rest = Reference().Replace(value, string.Empty);
        return schemeAllowed ? !SchemeOnly().IsMatch(rest) : rest.Trim().Length > 0;
    }

    /// <summary>A reference a node resolves: <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c>.</summary>
    [GeneratedRegex(@"\$\{(?:env|keyvault):[^{}\s]+\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    /// <summary>What may stand beside a credential's reference: nothing, or one scheme word before it.</summary>
    [GeneratedRegex(@"^\s*(?:[A-Za-z][A-Za-z0-9\-]*\s+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeOnly();
}
