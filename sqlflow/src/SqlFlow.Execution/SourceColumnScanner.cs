using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>
/// The <see cref="ComputeOperations.ScanSourceColumns"/> operation on a node: the file flow's document as the catalog
/// stores it, parsed by the same loader a run of the flow is, its relative location resolved against the flow's folder in
/// its repository's checkout (<see cref="DocumentLoader.ResolveRelativeLocation"/>) and a location that names a reference
/// resolved through the node's resolver (<see cref="SourceLocations.ResolveAsync(FlowDefinition, ISecretResolver, CancellationToken)"/>),
/// inside the scope of the values the control plane supplied with the task, exactly as a run of the flow is supplied
/// them. The flow's own reader then reads the newest files' headers (<see cref="ISourceReader.ScanFileColumnsAsync"/>)
/// with this node's identity, the identity its runs read the source with. Nothing of a row is read.
/// </summary>
public sealed class SourceColumnScanner
{
    private readonly YamlDocumentLoader _documents;
    private readonly IReadOnlyList<ISourceReader> _readers;
    private readonly ISecretResolver _secrets;

    public SourceColumnScanner(YamlDocumentLoader documents, IEnumerable<ISourceReader> readers, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(secrets);
        _documents = documents;
        _readers = readers.ToList();
        _secrets = secrets;
    }

    /// <summary>
    /// Reads the source of the flow <paramref name="payload"/> carries and sets its files' columns against the target the
    /// payload carries. The payload has been validated (<see cref="ComputeTaskPayload.Validate()"/>).
    /// </summary>
    /// <exception cref="SqlFlowException">The document is not a file flow's, its location is relative and the task names
    /// no checkout to resolve it against, a reference in it does not resolve, or no reader handles its source type.</exception>
    public async Task<SourceColumnsReport> ScanAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Operation != ComputeOperations.ScanSourceColumns || string.IsNullOrWhiteSpace(payload.Flow))
        {
            throw new SqlFlowException($"Only a {ComputeOperations.ScanSourceColumns} task carrying a flow document scans a source.");
        }

        var name = payload.SourceRef.Trim();
        var flowFile = payload.FlowFile ?? name + ".yaml";
        if (_documents.Parse(payload.Flow, flowFile) is not FileFlowDocument document)
        {
            throw new SqlFlowException($"Flow '{name}' is not a file flow. Only a file flow reads files, so only its source can be scanned.");
        }

        var flow = document.Flow;
        var written = flow.Source.Location;
        if (payload.FlowFile is null && IsRelativeFile(written))
        {
            throw new SqlFlowException(
                $"Flow '{name}' reads '{written}', relative to its own folder, and the task names no checkout of its repository to resolve it against.");
        }

        if (payload.FlowFile is not null)
        {
            flow = DocumentLoader.ResolveRelativeLocation(flow, payload.FlowFile);
        }

        flow = await SourceLocations.ResolveAsync(flow, _secrets, ct).ConfigureAwait(false);
        var reader = _readers.FirstOrDefault(r => r.CanHandle(flow.Source.Type))
            ?? throw new SqlFlowException($"No reader handles source type '{flow.Source.Type}' of flow '{name}'.");
        var scan = await reader
            .ScanFileColumnsAsync(flow.Source, payload.Files ?? ComputeTaskPayload.DefaultScanFiles, ct)
            .ConfigureAwait(false);

        return SourceColumnsReport.Describe(
            name, flow.Source.Type, written, scan, payload.ScanTarget, payload.Columns ?? []);
    }

    /// <summary>A location that is a path on disk relative to the flow's folder: not a URI, not a reference, not rooted.</summary>
    public static bool IsRelativeFile(string? location)
        => !string.IsNullOrWhiteSpace(location)
            && !location.Contains("://", StringComparison.Ordinal)
            && !SourceLocations.NamesReference(location)
            && !Path.IsPathRooted(location);
}
