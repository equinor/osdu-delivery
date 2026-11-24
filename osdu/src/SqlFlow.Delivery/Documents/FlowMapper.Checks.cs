using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

// The checks of a delivery flow once its blocks are mapped: its source, then what its route does with the cache and
// with change detection, the ranges of its reliability and parallelism, the partition header, its protocol options
// and its target's authentication. Each refusal names the key it is about.
internal static partial class FlowMapper
{
    private static void Validate(FlowDefinition flow, string source, KeyPaths paths)
    {
        ValidateSource(flow, source, paths);

        // A DSPDM row is no OSDU record a cache type could hold, so a dspdm route has nothing to put in the cache.
        if (flow.Target.Protocol == DeliveryProtocol.Dspdm && !flow.Cache.After.IsEmpty)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("cache.after")} is set, and the dspdm route writes DSPDM rows, which are no OSDU records a type of the partition's cache holds. Remove it.");
        }

        if (flow.Change.Detect == ChangeDetection.LastModified)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("change.detect")} cannot be lastModified. A document is always decided by the hash of what it renders to; the source row's business version column is source.lastModified.");
        }

        // Each key has its own name for comparing hashes. The other key's name would behave the same, which is exactly why
        // it is refused: the document would read as saying something it does not, and the editor documents one name only.
        if (flow.Change.Detect == ChangeDetection.ContentHash)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("change.detect")} is contentHash, which is how a payload is compared. A document is compared by the hash of what it renders to: write renderedHash, or always.");
        }

        if (flow.Change.PayloadDetect == ChangeDetection.RenderedHash)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("change.payloadDetect")} is renderedHash, which is how a document is compared. A payload is compared by its content hash: write contentHash, lastModified or always.");
        }

        // A ddms flow with no payload set of its own may send the bulk data an enrichment of its mapping names, and take the
        // watermark from those files; binding the flow to its mapping refuses it when no enrichment names any.
        if (flow.Change.PayloadDetect == ChangeDetection.LastModified && !SendsFiles(flow) && !MayTakeEnrichmentBulk(flow))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("change.payloadDetect")} is lastModified, but the {flow.Target.Protocol} protocol delivers no payload files to take the watermark from.");
        }

        if (flow.Reliability.Retry.Attempts < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.retry.attempts")} must be at least 1.");
        }

        if (flow.Parallelism.Nodes is < 1 or > FlowParallelism.MaxNodes)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("parallelism.nodes")} must be between 1 and {FlowParallelism.MaxNodes}: the most nodes a delivery is spread over, 1 keeping it on one."));
        }

        if (flow.Parallelism.MinRowsPerNode < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("parallelism.minRowsPerNode")} must be at least 1.");
        }

        if (flow.Parallelism.RecordsAtOnce < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("parallelism.recordsAtOnce")} must be at least 1.");
        }

        if (flow.Parallelism.RecordsPerBatch is < 1 or > FlowParallelism.MaxRecordsPerBatch)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("parallelism.recordsPerBatch")} must be between 1 and {FlowParallelism.MaxRecordsPerBatch}."));
        }

        if (flow.Parallelism.RenderThreads is < 0 or > FlowParallelism.MaxRenderThreads)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("parallelism.renderThreads")} must be between 0 and {FlowParallelism.MaxRenderThreads}."));
        }

        // Every OSDU service makes data-partition-id a required header (openapi storage v2, file v2, search v2,
        // workflow v1, schema-service v1). A flow that leaves it out authors a run where every single request comes
        // back 400 with a message about a tenant, which is a slow and confusing way to learn about a typo in the
        // flow. It costs nothing to say so while the document is being read. A flow that names its partitions, or leaves them
        // to the registry, leaves the header to the engine, which sets it to the partition each run targets; one written by
        // hand beside them could only disagree, and so could a dataPartition every id would be minted in.
        if (flow.Partitioned)
        {
            var which = flow.DeclaresPartitions ? "the flow names its partitions" : "the flow serves every partition registered with the catalog";
            if (flow.Target.Headers.ContainsKey(PartitionHeader))
            {
                throw new FlowValidationException(
                    $"{source}: target.headers names '{PartitionHeader}', and {which}: every run sets the header to the partition it targets. Remove the header.");
            }

            if (flow.Render.Parameters.ContainsKey(DeliveryDestination.DataPartitionParameter))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("render.parameters")} sets '{DeliveryDestination.DataPartitionParameter}', and {which}: every id a run mints is minted in the partition it targets. Remove the parameter.");
            }

            if (flow.Parameters.ContainsKey(PartitionNames.RunValue))
            {
                throw new FlowValidationException(
                    $"{source}: parameters declares '{PartitionNames.RunValue}', and {which}: a run names the partition it targets under that value. Rename the parameter.");
            }

            // A cache version is a version of one partition's cache, so a pin means something for a flow of one partition only.
            if ((flow.Partitions.Count > 1 || flow.FollowsRegistry) && !flow.Render.CacheVersion.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(flow.FollowsRegistry
                    ? $"{source}: {paths.Shared("render.cacheVersion")} pins version {flow.Render.CacheVersion}, which is a version of one partition's cache, and the flow serves every registered partition, each rendering against its own partition's cache. Remove render.cacheVersion, or name the flow's one partition under partitions."
                    : $"{source}: {paths.Shared("render.cacheVersion")} pins version {flow.Render.CacheVersion}, which is a version of one partition's cache, and the flow names {flow.Partitions.Count} partitions, each rendering against its own partition's cache. Remove render.cacheVersion, or name one partition.");
            }
        }
        else
        {
            // The flow's partition is its header's: it is hard-coded there, and has to name one.
            if (string.IsNullOrWhiteSpace(flow.Target.Headers[PartitionHeader]))
            {
                throw new FlowValidationException($"{source}: target.headers.{PartitionHeader} must not be empty.");
            }
        }

        if (flow.Target.ProtocolOptions.BatchSize is < 1 or > ProtocolOptions.MaxBatchSize)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.batchSize")} must be between 1 and {ProtocolOptions.MaxBatchSize}.");
        }

        if (flow.Target.ProtocolOptions.DdmsRoot is { } ddmsRoot)
        {
            if (!DeliveryProtocols.ReachesDdms(flow.Target.Protocol))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.ddmsRoot")} only applies to the ddms route; the {DeliveryProtocols.Name(flow.Target.Protocol)} route reaches its services under the endpoint already.");
            }

            if (ddmsRoot.Length == 0 || ddmsRoot[0] != '/' || ddmsRoot.Contains("://", StringComparison.Ordinal) || ddmsRoot.Any(char.IsWhiteSpace))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.ddmsRoot")} '{ddmsRoot}' must be a path under the endpoint starting with '/', such as /api/os-wellbore-ddms.");
            }
        }

        if (flow.Target.ProtocolOptions.LegalValidatePath is { } legalPath
            && legalPath[0] != '/'
            && !(Uri.TryCreate(legalPath, UriKind.Absolute, out var legalUrl) && legalUrl.Scheme is "http" or "https"))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.legalValidatePath")} '{legalPath}' must be a path under the endpoint starting with '/', or an absolute http(s) URL.");
        }

        if (flow.Target.ProtocolOptions.RegisterPath is { } registerPath)
        {
            if (!flow.Target.Ddms.Any(d => d.Registration is not null))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.registerPath")} says where the Register service reads a DDMS registration, and no DDMS under target.ddms names one with register.");
            }

            if (!registerPath.Contains("{id}", StringComparison.Ordinal)
                || (registerPath[0] != '/' && !(Uri.TryCreate(registerPath, UriKind.Absolute, out var registerUrl) && registerUrl.Scheme is "http" or "https")))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.registerPath")} '{registerPath}' must be a path under the endpoint starting with '/', or an absolute http(s) URL, with {{id}} where the registration's id goes.");
            }
        }

        // The bulk endpoint replaces the whole bulk, so at most one chunk can go to it; more than one is a session.
        // A flow that asked for a higher threshold was asking for chunks to overwrite each other.
        if (flow.Target.ProtocolOptions.SessionThresholdChunks is < 0 or > 1)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.sessionThresholdChunks")} must be 1 (a single chunk goes straight to the bulk endpoint, more open a session) or 0 (always open a session). "
                + "The bulk endpoint replaces the whole bulk on every write, so several chunks sent to it would overwrite each other.");
        }

        if (flow.Target.ProtocolOptions.MaxChunkValues < 0 || flow.Target.ProtocolOptions.MaxChunkColumns < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.maxChunkValues")} and maxChunkColumns must not be negative (0 does not check the chunk shape).");
        }

        if (flow.Target.ProtocolOptions.WorkflowPollSeconds < 1 || flow.Target.ProtocolOptions.WorkflowTimeoutMinutes < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.workflowPollSeconds")} and workflowTimeoutMinutes must be at least 1.");
        }

        if (flow.Target.ProtocolOptions.DatasetIndexWaitSeconds < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.datasetIndexWaitSeconds")} must not be negative (0 does not wait).");
        }

        // Every upload states its type in a Content-Type header, which a malformed value cannot be written into.
        foreach (var (key, value) in new[] { ("payloadContentType", flow.Target.ProtocolOptions.PayloadContentType), ("filesContentType", flow.Target.ProtocolOptions.FilesContentType) })
        {
            if (value is not null && !IsMediaType(value))
            {
                throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions." + key)} '{value}' is not a media type such as application/octet-stream.");
            }
        }

        // RAFS reads the version from the query as major.minor or major.minor.patch (osdu/specs/rafs-ddms/INTEGRATION.md section 1.2).
        if (!System.Text.RegularExpressions.Regex.IsMatch(flow.Target.ProtocolOptions.ContentSchemaVersion, @"^\d+\.\d+(?:\.\d+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.contentSchemaVersion")} '{flow.Target.ProtocolOptions.ContentSchemaVersion}' is not a content schema version such as 1.0.0.");
        }

        if (flow.Target.ProtocolOptions.UploadUrlExpiry is { } expiry && !ValidExpiry(expiry))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.uploadUrlExpiry")} '{expiry}' must be a whole number of minutes, hours or days, such as 30M, 12H or 2D.");
        }

        if (flow.Target.ProtocolOptions.UploadBlockMiB is < 1 or > ProtocolOptions.MaxUploadBlockMiB)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.uploadBlockMiB")} must be 1 to {ProtocolOptions.MaxUploadBlockMiB}.");
        }

        if (flow.Target.ProtocolOptions.UploadConcurrency is < 1 or > ProtocolOptions.MaxUploadConcurrency)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.uploadConcurrency")} must be 1 to {ProtocolOptions.MaxUploadConcurrency}.");
        }

        // Both are written onto records the target stores (a dataset record per file, the manifest the workflow
        // ingests), so they answer to the same pattern as a mapping's kind, and are checked while the flow is read
        // rather than on the first delivery that needs them.
        foreach (var (key, value) in new[] { ("datasetKind", flow.Target.ProtocolOptions.DatasetKind), ("manifestKind", flow.Target.ProtocolOptions.ManifestKind) })
        {
            if (!IsRecordKind(value))
            {
                throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions." + key)} '{value}' must be 'authority:source:entityType:major.minor.patch'.");
            }
        }

        if (flow.Target.ProtocolOptions.ManifestSection is { } section && !ProtocolOptions.ManifestSections.Contains(section, StringComparer.Ordinal))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.manifestSection")} '{section}' is not one of {string.Join(", ", ProtocolOptions.ManifestSections)}.");
        }

        if (string.IsNullOrWhiteSpace(flow.Target.ProtocolOptions.WorkflowAppKey))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.workflowAppKey")} must not be empty.");
        }

        ValidateReferenceOptions(flow, source, paths);
        ValidateWorkflow(flow, source, paths);

        if (flow.Target.Auth.Type == TargetAuthType.OAuth2ClientCredentials && flow.Target.Auth.Token is null)
        {
            throw new FlowValidationException($"{source}: target.auth of type oauth2ClientCredentials needs a 'token' block with the token endpoint url and body.");
        }

        if (flow.Target.Auth.Type is TargetAuthType.Bearer or TargetAuthType.ApiKeyHeader or TargetAuthType.Basic && string.IsNullOrWhiteSpace(flow.Target.Auth.SecretRef))
        {
            throw new FlowValidationException($"{source}: target.auth of type {flow.Target.Auth.Type} needs secretRef.");
        }

        if (flow.Target.Auth.Type == TargetAuthType.ApiKeyHeader && string.IsNullOrWhiteSpace(flow.Target.Auth.HeaderName))
        {
            throw new FlowValidationException($"{source}: target.auth of type apiKeyHeader needs headerName.");
        }
    }

    /// <summary>The file service's expiryTime shape: a whole number of minutes, hours or days.</summary>
    private static bool ValidExpiry(string expiry)
        => expiry.Length >= 2 && expiry[^1] is 'M' or 'H' or 'D' && expiry[..^1].All(char.IsAsciiDigit);

    /// <summary>A bare media type (<c>type/subtype</c>, parameters allowed), as a Content-Type header takes it.</summary>
    private static bool IsMediaType(string value)
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value, out var parsed) || parsed.MediaType is not { } media)
        {
            return false;
        }

        var slash = media.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < media.Length - 1;
    }
}
