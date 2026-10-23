using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Configuration;

/// <summary>
/// How fast approved cache changes are carried out to the estate (section <c>Osdu:CacheRollout</c>). When a cached
/// value moves, every record built from it has to be delivered again, and one corrected unit can reach millions of
/// rows: the rollout marks them in bounded batches instead of one statement, and these settings are the pace.
/// </summary>
public sealed class CacheRolloutOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:CacheRollout";

    /// <summary>Turns the automatic rollout off. Approved changes then wait, and the GUI still shows what is queued.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Records marked for redelivery per batch.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>Batches per tick, across all approved changes.</summary>
    public int BatchesPerPass { get; set; } = 2;

    /// <summary>Seconds between ticks.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        if (BatchSize < 1)
        {
            throw new InvalidOperationException($"{SectionName}:BatchSize must be positive.");
        }

        if (BatchesPerPass < 1)
        {
            throw new InvalidOperationException($"{SectionName}:BatchesPerPass must be positive.");
        }

        if (PollSeconds < 1)
        {
            throw new InvalidOperationException($"{SectionName}:PollSeconds must be positive.");
        }
    }
}

/// <summary>
/// The scheduled target probe (section <c>Osdu:TargetProbe</c>): how often the deployment asks every active delivery
/// flow's OSDU whether it still answers with that flow's credentials, so an operator and an alert see a target go away
/// without anyone pressing "Probe target".
/// <para>Off by default, and deliberately so: one probe is a token exchange and a request against a live OSDU, for every
/// interface of every active delivery flow, every interval. Switch it on where that cost is understood, and use
/// <see cref="Pipelines"/> to narrow it to the flows that matter.</para>
/// </summary>
public sealed class TargetProbeOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:TargetProbe";

    /// <summary>The floor under <see cref="IntervalMinutes"/>: below this the probes cost more than they tell.</summary>
    public const int MinimumIntervalMinutes = 5;

    /// <summary>Runs the scheduled probe. Off by default, since every pass reaches a live OSDU.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minutes between passes. Default 15, and never below <see cref="MinimumIntervalMinutes"/>.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Interfaces probed in one pass, across every flow. Default 200.</summary>
    public int MaxPerPass { get; set; } = 200;

    /// <summary>
    /// The delivery flows to probe, by pipeline name, comma separated. Empty means every active delivery pipeline. A
    /// name no active delivery pipeline carries is reported in the pass's log rather than silently probing nothing.
    /// </summary>
    public string? Pipelines { get; set; }

    /// <summary>The flows <see cref="Pipelines"/> names, trimmed and deduplicated; empty for every active delivery pipeline.</summary>
    public IReadOnlyList<string> PipelineNames()
        => string.IsNullOrWhiteSpace(Pipelines)
            ? []
            : Pipelines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        if (IntervalMinutes < MinimumIntervalMinutes || IntervalMinutes > 1440)
        {
            throw new InvalidOperationException(
                $"{SectionName}:IntervalMinutes must be between {MinimumIntervalMinutes} and 1440 (a day): every pass costs a token " +
                "exchange and a request against a live OSDU for each interface of each active delivery flow.");
        }

        if (MaxPerPass is < 1 or > 1000)
        {
            throw new InvalidOperationException($"{SectionName}:MaxPerPass must be between 1 and 1000.");
        }

        if (!string.IsNullOrWhiteSpace(Pipelines) && PipelineNames().Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Pipelines names no flow. Leave it empty to probe every active delivery pipeline, or list the pipeline names, comma separated.");
        }
    }
}

/// <summary>
/// The OSDU data definitions the Templates page browses (section <c>Osdu:SchemaRepository</c>): the Open Group's
/// public repository of OSDU schemas, read through its GitLab API into a local copy on disk, one release at a time. The
/// defaults are the public repository; point both URLs at a mirror of it when the control plane cannot reach
/// community.opengroup.org.
/// </summary>
public sealed class SchemaRepositoryOptions
{
    /// <summary>The configuration section the control plane module binds this from.</summary>
    public const string SectionName = "Osdu:SchemaRepository";

    /// <summary>The GitLab API URL of the data definitions project.</summary>
    public Uri ApiUrl { get; set; } = OsduDataDefinitions.DefaultApiUrl;

    /// <summary>The project's web page, which the page links a release's schema files on.</summary>
    public Uri WebUrl { get; set; } = OsduDataDefinitions.DefaultWebUrl;

    /// <summary>Where the local copy lives; empty means <c>sqlflow/osdu-data-definitions</c> under the temp folder.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Minutes the release list may age before it is read again from the repository; a sync reads it at once. Default a day.</summary>
    public int RefreshMinutes { get; set; } = 1440;

    /// <summary>Minutes downloading one release may take. Default 15.</summary>
    public int DownloadTimeoutMinutes { get; set; } = 15;

    /// <summary>Brings the local copy up (the release list, and the newest release when it is not on disk) when the control plane starts. Default on.</summary>
    public bool WarmOnStart { get; set; } = true;

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        RequireHttpUrl(ApiUrl, nameof(ApiUrl));
        RequireHttpUrl(WebUrl, nameof(WebUrl));
        if (RefreshMinutes is < 1 or > 10080)
        {
            throw new InvalidOperationException($"{SectionName}:RefreshMinutes must be between 1 and 10080 (a week).");
        }

        if (DownloadTimeoutMinutes is < 1 or > 240)
        {
            throw new InvalidOperationException($"{SectionName}:DownloadTimeoutMinutes must be between 1 and 240.");
        }
    }

    private static void RequireHttpUrl(Uri? url, string name)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be an absolute http or https URL.");
        }
    }
}

/// <summary>
/// The OSDU partitions this deployment delivers to (section <c>Osdu:Partitions</c>, one child per data-partition-id), with
/// the values their runs resolve their flows' references with. The control plane registers them and writes their values
/// when it starts (<see cref="ConfiguredPartitions"/>), so a deployment comes up with its partitions in place, and keeps
/// them as the configuration says: the Partitions page, the API and the CLI do not change what it sets.
/// </summary>
/// <example>
/// <code>
/// Osdu__Partitions__dev__Default=true
/// Osdu__Partitions__dev__Description=The development platform
/// Osdu__Partitions__dev__Properties__OSDU_URL=https://osdu-dev.example.com
/// Osdu__Partitions__dev__Properties__OSDU_CLIENT_SECRET=${keyvault:estate-vault/osdu-dev-client-secret}
/// </code>
/// </example>
public sealed class ConfiguredPartitionsOptions
{
    /// <summary>
    /// The section the control plane module binds this from: the module's own, whose <c>Partitions</c> child holds the
    /// partitions, so a partition's settings are spelled <c>Osdu:Partitions:&lt;id&gt;:...</c>.
    /// </summary>
    public const string SectionName = "Osdu";

    /// <summary>The partitions, by data-partition-id. Empty registers none and leaves the registry as it is.</summary>
    public Dictionary<string, ConfiguredPartitionOptions?> Partitions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The values set for no partition (<c>Osdu:Properties:&lt;NAME&gt;</c>), by the name a flow spells in <c>${env:NAME}</c>:
    /// what every run of every kind resolves with, a pre-ingestion or ingestion flow's connections as much as a delivery's
    /// platform, so the deployment's nodes need no environment of their own. A secret is given as a reference, never as
    /// its value, and a connection string connects with an identity or names its password as a reference.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The values set for no partition as the control plane writes them, checked.</summary>
    /// <exception cref="InvalidOperationException">A value is not one the configuration may hold; the message names the setting.</exception>
    public IReadOnlyDictionary<string, string> ConfiguredProperties() => ConfiguredPartitions.CheckProperties(Properties);

    /// <summary>The partitions as the control plane writes them, checked.</summary>
    /// <exception cref="InvalidOperationException">A partition or a value is not one the configuration may hold; the message names the setting.</exception>
    public IReadOnlyList<ConfiguredPartition> Configured()
        => ConfiguredPartitions.Check(Partitions.Select(p => new ConfiguredPartition(
            p.Key,
            p.Value?.Description,
            p.Value?.Default ?? false,
            p.Value?.Properties ?? new Dictionary<string, string>(StringComparer.Ordinal))));

    /// <exception cref="InvalidOperationException">A partition or a value is not one the configuration may hold; the message names the setting.</exception>
    public void Validate()
    {
        _ = Configured();
        _ = ConfiguredProperties();
    }
}

/// <summary>One partition of <see cref="ConfiguredPartitionsOptions"/>.</summary>
public sealed class ConfiguredPartitionOptions
{
    /// <summary>
    /// True for the partition a run that names none runs in. One partition is the default; when only one is configured, it
    /// is the default without saying so.
    /// </summary>
    public bool Default { get; set; }

    /// <summary>What the partition is for (the environment, the platform), shown wherever partitions are listed.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The values a run bound to the partition resolves its flows' <c>${env:NAME}</c> references with, by name: the
    /// endpoint, the token URL and scope, the client id, the ACLs, the legal tag. A secret is given as a
    /// <c>${keyvault:vault/secret}</c> reference, never as its value.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
}
