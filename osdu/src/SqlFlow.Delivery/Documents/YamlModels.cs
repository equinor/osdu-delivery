// Mutable YAML shapes. They exist only so YamlDotNet can populate them; the loader maps them to the immutable model
// records in SqlFlow.Delivery.Model and validates as it goes. Every property must be listed: unknown keys are rejected.
// The platform envelope keys (name, batch, description, schedule, mode, lifecycle) are listed too: the platform
// parses them itself, the delivery loader only has to let them pass.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class DocumentProbeYaml
{
    public string? FlowType { get; set; }

    public string? DocumentType { get; set; }
}

/// <summary>The <c>telemetry</c> block of a flow: what its runs' correlation id names (osdu/docs/reference/concepts/request-correlation.md).</summary>
internal sealed class TelemetryYaml
{
    public string? Operation { get; set; }
}

/// <summary>A repository document (<c>documentType: repository</c>, osdu/docs/reference/flow/repository.md).</summary>
internal sealed class RepositoryYaml
{
    public string? DocumentType { get; set; }

    public string? Code { get; set; }

    public string? Description { get; set; }

    public string? Team { get; set; }
}

internal class FlowParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }

    public List<string>? Choices { get; set; }
}

internal sealed class FlowParameterLookupYaml
{
    public string? Column { get; set; }

    public string? Interface { get; set; }
}

internal sealed class TargetAuthYaml
{
    public string? Type { get; set; }

    public string? SecretRef { get; set; }

    public string? SecondarySecretRef { get; set; }

    public string? HeaderName { get; set; }

    public string? ValuePrefix { get; set; }

    public TokenEndpointYaml? Token { get; set; }
}

internal sealed class TokenEndpointYaml
{
    public string? Url { get; set; }

    public string? DiscoveryUrl { get; set; }

    public Dictionary<string, string>? Body { get; set; }

    public bool BasicAuthClient { get; set; }

    public string? TokenPath { get; set; }

    public string? ApplyPrefix { get; set; }
}

/// <summary>
/// The <c>reliability</c> block: what makes a flow's requests hold. The settings that moved to <c>parallelism</c> are read
/// only so the loader can refuse each with the key it is now written as (<see cref="FlowMapper"/>), rather than with a bare
/// "unknown key".
/// </summary>
internal sealed class FlowReliabilityYaml
{
    public FlowRetryYaml? Retry { get; set; }

    public List<int>? SkipStatusCodes { get; set; }

    public int? TimeoutSeconds { get; set; }

    public double? RateLimitRps { get; set; }

    public bool? VerifyTls { get; set; }

    public List<string>? UrlAllowlist { get; set; }

    public long? MaxResponseBytes { get; set; }

    public long? MaxRequestBodyBytes { get; set; }

    public int? LeaseSeconds { get; set; }

    /// <summary>Moved to <c>parallelism.recordsAtOnce</c>; refused here.</summary>
    public int? Concurrency { get; set; }

    /// <summary>Gone: retries are claimed <c>parallelism.recordsAtOnce</c> at a time; refused here.</summary>
    public int? BatchSize { get; set; }

    /// <summary>Moved to <c>parallelism.recordsPerBatch</c>; refused here.</summary>
    public int? BatchRecords { get; set; }

    /// <summary>Moved to <c>parallelism.nodes</c>; refused here.</summary>
    public int? FanOut { get; set; }

    /// <summary>Moved to <c>parallelism.minRowsPerNode</c>; refused here.</summary>
    public int? FanOutMinRecords { get; set; }

    /// <summary>Moved to <c>parallelism.renderThreads</c>; refused here.</summary>
    public int? RenderParallelism { get; set; }

    /// <summary>Moved to <c>parallelism.interfacesAtOnce</c>; refused here.</summary>
    public int? ParallelInterfaces { get; set; }
}

/// <summary>
/// The <c>parallelism</c> block of an assertion, dimension, inventory or retrieval flow: what one run works on at once,
/// written <see cref="RecordsAtOnce"/> as on a delivery flow.
/// </summary>
internal sealed class RunParallelismYaml
{
    public int? RecordsAtOnce { get; set; }

    /// <summary>Renamed <see cref="RecordsAtOnce"/>; refused here.</summary>
    public int? Concurrency { get; set; }
}

internal sealed class FlowRetryYaml
{
    public int? Attempts { get; set; }

    public string? Backoff { get; set; }

    public int? BaseDelayMs { get; set; }

    public int? MaxDelayMs { get; set; }

    public bool? HonorRetryAfter { get; set; }

    public int? RecordBaseDelayMinutes { get; set; }

    public int? RecordMaxDelayMinutes { get; set; }
}
