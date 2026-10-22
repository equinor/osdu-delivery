using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// A partition's own connection to OSDU (osdu/docs/reference/concepts/explorer.md, How it reaches OSDU): the platform and
/// the client the deployment names for the partition, by the values its runs resolve their <c>${env:NAME}</c> references
/// with (osdu/docs/reference/concepts/partitions.md, Configured partitions), and no flow. The explorer reads a partition
/// through it, so a deployment can be searched before a repository is synced or a flow delivers anywhere.
/// </summary>
/// <remarks>
/// The connection is the five values the deployment templates and the local setup give every partition: the platform's
/// base URL, the token endpoint, the scope, the client id and the client secret, each read as a run reads
/// <c>${env:NAME}</c>: the control plane's value for the partition, then its value for no partition, then the control
/// plane's own environment. The client signs in with the OAuth 2.0 client credentials grant, every request carries
/// <c>data-partition-id</c> set to the partition, and the reads go to the platform's own services under the base URL.
/// </remarks>
public static class PartitionConnection
{
    /// <summary>The platform's base URL.</summary>
    public const string UrlName = "OSDU_URL";

    /// <summary>The token endpoint the client signs in at.</summary>
    public const string TokenUrlName = "OSDU_TOKEN_URL";

    /// <summary>The scope the token is asked for.</summary>
    public const string ScopeName = "OSDU_SCOPE";

    /// <summary>The client (application) id.</summary>
    public const string ClientIdName = "OSDU_CLIENT_ID";

    /// <summary>The client secret, a <c>${keyvault:...}</c> or <c>${env:...}</c> reference wherever it is configured.</summary>
    public const string ClientSecretName = "OSDU_CLIENT_SECRET";

    /// <summary>Every value the connection is made of, in the order a message names them.</summary>
    public static IReadOnlyList<string> Names { get; } = [UrlName, TokenUrlName, ScopeName, ClientIdName, ClientSecretName];

    /// <summary>How a log line and an answer name the connection of <paramref name="partition"/>.</summary>
    public static string LabelOf(string partition) => $"partition {partition}";

    /// <summary>The connection of <paramref name="partition"/>, every value a reference its reads resolve.</summary>
    /// <exception cref="ArgumentException">The partition is not a partition id written literally.</exception>
    public static OsduReadTarget Of(string partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (!CacheScope.IsPartitionId(partition))
        {
            throw new ArgumentException($"'{partition}' is not a partition id.", nameof(partition));
        }

        var target = new FlowTarget
        {
            Endpoint = Reference(UrlName),
            Auth = new TargetAuth
            {
                Type = TargetAuthType.OAuth2ClientCredentials,
                SecretRef = Reference(ClientSecretName),
                SecondarySecretRef = Reference(ClientIdName),
                Token = new TargetTokenEndpoint
                {
                    Url = Reference(TokenUrlName),
                    Body = new Dictionary<string, string>(StringComparer.Ordinal) { ["scope"] = Reference(ScopeName) },
                },
            },
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = partition },

            // The platform's own services, at their own paths under the base URL: what the explorer reads.
            Protocol = DeliveryProtocol.Storage,
        };
        return new OsduReadTarget(LabelOf(partition), partition, target, new FlowReliability());
    }

    /// <summary>
    /// The names of <see cref="Names"/> that neither <paramref name="configured"/> (the control plane's values for the
    /// partition over its values for none) nor <paramref name="environment"/> (the control plane's own) holds, in the order
    /// <see cref="Names"/> lists them; empty when the connection can be made. A configured value that is its own reference
    /// (<c>OSDU_URL = ${env:OSDU_URL}</c>) leaves the name to the environment, as a run reads it; any other reference counts
    /// as set, and what it resolves to is the read's to find.
    /// </summary>
    public static IReadOnlyList<string> Missing(IReadOnlyDictionary<string, string> configured, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(environment);
        return Names.Where(name => ValueOf(name, configured, environment) is null).ToList();
    }

    /// <summary>
    /// Where the connection reaches, as a person is shown it: the configured base URL, or the environment's, as scheme, host,
    /// port and path, without the credentials, query or fragment a URL can carry; a configured reference as written, since
    /// it is resolved only when a read needs it. Null when no base URL is set, or the one set is not an absolute HTTP URL.
    /// </summary>
    public static string? EndpointShown(IReadOnlyDictionary<string, string> configured, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(environment);
        if (ValueOf(UrlName, configured, environment) is not { } value)
        {
            return null;
        }

        if (IsReference(value))
        {
            return value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : null;
    }

    /// <summary>The value <paramref name="name"/> takes before any reference in it is resolved, or null when nothing sets it.</summary>
    private static string? ValueOf(string name, IReadOnlyDictionary<string, string> configured, Func<string, string?> environment)
    {
        if (configured.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), Reference(name), StringComparison.Ordinal))
        {
            return value.Trim();
        }

        return environment(name) is { } own && !string.IsNullOrWhiteSpace(own) ? own.Trim() : null;
    }

    private static bool IsReference(string value) => value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}');

    private static string Reference(string name) => "${env:" + name + "}";
}
