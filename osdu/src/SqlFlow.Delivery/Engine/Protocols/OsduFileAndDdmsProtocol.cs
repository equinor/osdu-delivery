using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The composed route of files and DDMS bulk data (osdu/docs/reference/flow/routes.md, fileAndDdms and
/// manifestAndDdms): each record's files are uploaded and registered through the file service, as the file route does;
/// the record is written through the collection of its DDMS with its dataset list pointing at them, as the ddms route
/// does; then its bulk data. Each part goes when its content changed or a redelivery names it
/// (<see cref="DeliveryWork.Sends"/>), so new bulk data does not upload the files again, and every file step resumes on
/// a retry. The record and its bulk data are checked against the DDMS's rules before the first file is uploaded.
/// </summary>
public sealed class OsduFileAndDdmsProtocol : IDeliveryProtocol
{
    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly ProtocolOptions _files;
    private readonly OsduDdmsProtocol _ddms;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;

    public OsduFileAndDdmsProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _files = options.ForFiles(besideBulk: true);
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
        _ddms = new OsduDdmsProtocol(client, options, logger, requestBodyCeiling, _time, routing);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.FileAndDdms;

    /// <summary>The files are registered before the record goes to its DDMS, and the record before its bulk data.</summary>
    public bool Undoes => true;

    /// <summary>
    /// Undoes what unfinished deliveries left (osdu/docs/reference/concepts/artifacts-and-undo.md, fileAndDdms and
    /// manifestAndDdms): what the record's DDMS made (a session abandoned, and the record removed or given back the
    /// version it replaced), then the datasets the record's files were registered as, removed reversibly through
    /// storage (a registration whose answer was lost found by where its file landed). The record goes first, so it
    /// never names datasets already removed.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var results = new List<UndoResult>();
        var undone = new List<DatasetUndo>();
        foreach (var work in works)
        {
            var datasets = work.Items.Where(i => i.Artifact.Role == ArtifactRoles.Dataset).ToList();
            var ddms = work.Items.Except(datasets).ToList();

            // The record (and what its DDMS made beside it) goes back first, so a record that cannot be taken back yet never
            // names datasets already removed: its datasets wait with it.
            var record = ddms.Count > 0 ? await _ddms.UndoRecordAsync(work with { Items = ddms }, ct).ConfigureAwait(false) : [];
            results.AddRange(record);
            if (datasets.Count > 0)
            {
                var recordResults = record.Where(r => ArtifactRoles.IsTheRecord(r.Item.Artifact.Role)).ToList();
                if (ArtifactUndo.WaitForRecord(recordResults) is { } blocked)
                {
                    results.AddRange(datasets.Select(i => UndoResult.Failed(i, blocked)));
                    continue;
                }

                undone.Add(new DatasetUndo(work, datasets));
            }
        }

        // The datasets of every record that went back are removed together, in as few storage calls as storage takes.
        if (undone.Count > 0)
        {
            _storage ??= new OsduRecordProtocol(_client, RecordRestores.WriterOptions(new ProtocolOptions { BatchSize = _options.BatchSize }), _time);
            results.AddRange(await ArtifactUndo.DatasetsAsync(_client, _files, _storage, undone, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>The storage service's writer an undo removes the files' datasets through, made when an undo first needs it.</summary>
    private OsduRecordProtocol? _storage;

    /// <summary>Where the protocol sends each record: the flow's DDMSs, every registration among them read.</summary>
    public DdmsRouting Routing => _ddms.Routing;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        // A delivery of the record alone carries no parts; one that carries a payload lists both.
        var files = work.DeliverPayload ? Part(work, PayloadParts.Files) : null;
        var bulk = work.DeliverPayload ? Part(work, PayloadParts.Bulk) : null;
        var sendsFiles = files is not null && work.Sends(files);
        var sendsBulk = bulk is not null && work.Sends(bulk);
        if (!work.DeliverMetadata && !sendsFiles && !sendsBulk)
        {
            return new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion, Detail = "every part is as OSDU holds it" };
        }

        // The DDMS's rules and the bulk data's chunks are checked first, so a record the DDMS would refuse is held before
        // any file is uploaded for it. The record is written whenever its files move, since its dataset list moves with them.
        var ddmsWork = work with
        {
            DeliverMetadata = work.DeliverMetadata || sendsFiles,
            DeliverPayload = sendsBulk,
            Payload = bulk?.Source,
            Parts = [],
            ForcedParts = new HashSet<string>(StringComparer.Ordinal),
        };
        var prepared = await _ddms.PrepareAsync(ddmsWork, ct).ConfigureAwait(false);

        var steps = new DeliverySteps(_time);
        IReadOnlyList<string> datasets;
        var uploaded = 0;
        if (sendsFiles)
        {
            var source = files!.Source ?? throw new RecordHeldException($"payload '{files.Payload}' names no files for the record");
            (datasets, uploaded) = await FileUploads.UploadAndRegisterAsync(_client, _files, work, source, _requestBodyCeiling, steps, _time, ct).ConfigureAwait(false);
        }
        else
        {
            datasets = FileUploads.DatasetIds(work.TargetState);
        }

        var document = (JsonObject)work.Document.DeepClone();
        if (datasets.Count > 0)
        {
            FileUploads.SetDatasets(document, _options.DatasetsProperty, datasets);
        }

        DeliveryOutcome written;
        try
        {
            written = await _ddms.SendAsync(ddmsWork with { Document = document }, prepared, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            return DeliveryOutcome.Failed(ex, steps.Steps);
        }

        var returned = new Dictionary<string, string>(written.Returned, StringComparer.Ordinal);
        if (datasets.Count > 0)
        {
            returned[FileUploads.DatasetIdsValue] = string.Join(",", datasets);
        }

        if (sendsFiles)
        {
            returned["files"] = uploaded.ToString(CultureInfo.InvariantCulture);
            returned[PayloadParts.StateKey(files!.Payload)] = files.Hash;
        }

        if (sendsBulk)
        {
            returned[PayloadParts.StateKey(bulk!.Payload)] = bulk.Hash;
        }

        var detail = new List<string>();
        if (uploaded > 0)
        {
            detail.Add($"{uploaded.ToString(CultureInfo.InvariantCulture)} file(s) uploaded and registered");
        }

        if (written.Detail is { } bulkDetail)
        {
            detail.Add(bulkDetail);
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = written.MetadataDelivered,
            PayloadDelivered = sendsFiles || written.PayloadDelivered,
            TargetVersion = written.TargetVersion,
            ChunksSent = uploaded + written.ChunksSent,
            Detail = detail.Count == 0 ? null : string.Join("; ", detail),
            Returned = returned,
            Steps = [.. steps.Steps, .. written.Steps],

            // Files that went registered new datasets the record now names; the ones its earlier delivery named stay live.
            Superseded = sendsFiles ? FileUploads.DatasetIds(work.TargetState).Except(datasets, StringComparer.Ordinal).ToList() : [],
        };
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => _ddms.VerifyAsync(targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => _ddms.ReadAsync(targetId, ct);

    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => _ddms.VersionsAsync(targetId, ct);

    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => _ddms.ReadVersionAsync(targetId, version, ct);

    /// <summary>The file service and every DDMS the flow reaches; the target is reachable when all of them answer.</summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var file = await RecordWriter.ProbeAsync(_client, FileUploads.DefaultFileProbePath, ct).ConfigureAwait(false);
        if (!file.Reachable)
        {
            return file;
        }

        var ddms = await _ddms.ProbeAsync(ct).ConfigureAwait(false);
        return ddms.Reachable ? ddms with { Path = file.Path + ", " + ddms.Path } : ddms;
    }

    public Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
        => _ddms.InvalidLegalTagsAsync(tags, ct);

    /// <summary>
    /// The DDMS's removal of the record (as the ddms route removes it), and, when everything goes, the dataset records its
    /// files were registered as, with the files (as the file route removes them).
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        var outcome = await _ddms.DeleteAsync(targetId, scope, targetState, ct).ConfigureAwait(false);
        return scope == RemovalScope.Everything
            ? await FileUploads.WithDatasetsDeletedAsync(_client, _options, outcome, targetState, ct).ConfigureAwait(false)
            : outcome;
    }

    /// <summary>The part of the record's payload a composed route reads by its role; a work without it was planned for another route.</summary>
    internal static WorkPayloadPart Part(DeliveryWork work, string role)
        => work.Parts.FirstOrDefault(p => string.Equals(p.Role, role, StringComparison.Ordinal))
            ?? throw new RecordHeldException($"the record's pending payload lists no {role} part; it was planned for another route, so redeliver it to plan it again");

    /// <summary>The part of the record's payload a composed route reads by its role, or null when the flow sends none.</summary>
    internal static WorkPayloadPart? OptionalPart(DeliveryWork work, string role)
        => work.Parts.FirstOrDefault(p => string.Equals(p.Role, role, StringComparison.Ordinal));
}
