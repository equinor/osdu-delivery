using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The ddms route (osdu/docs/reference/flow/routes.md, ddms; osdu/docs/reference/flow/ddms.md, Where a record goes):
/// each record goes to the collection of the DDMS serving its entity type (<see cref="DdmsRouting"/>), by the call
/// pattern of that DDMS's shape: the Wellbore DDMS v3 (<see cref="WellboreDdmsV3Shape"/>), the Well Delivery DDMS
/// (<see cref="WellDeliveryShape"/>), the Rock and Fluid Sample DDMS (<see cref="RafsShape"/>), the Production DDMS
/// historian (<see cref="ProductionTimeSeriesShape"/>), Seismic Store (<see cref="SeismicStoreShape"/>) or the Reservoir
/// Management DDMS (<see cref="ReservoirManagementShape"/>). A record's shape
/// checks it, and the data its DDMS keeps for it, before the first request, writes both, and says how the record is read
/// back, verified and removed. The protocol is named after the <c>osduWellLog</c> value a flow's <c>target.protocol</c>
/// gives it.
/// </summary>
public sealed class OsduDdmsProtocol : IDeliveryProtocol
{
    public const string MetadataStep = "metadata";
    public const string PayloadStep = "payload";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly DdmsRouting _routing;
    private readonly TimeProvider _time;
    private readonly IDdmsShape _wellbore;
    private readonly IDdmsShape _wellDelivery;
    private readonly IDdmsShape _rafs;
    private readonly IDdmsShape _timeSeries;
    private readonly IDdmsShape _seismic;
    private readonly IDdmsShape _reservoirManagement;

    public OsduDdmsProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _routing = routing ?? DdmsRouting.Of(options);
        _time = time ?? TimeProvider.System;
        var context = new DdmsShapeContext(client, options, _routing, logger, requestBodyCeiling, _time);
        _wellbore = new WellboreDdmsV3Shape(context);
        _wellDelivery = new WellDeliveryShape(context);
        _rafs = new RafsShape(context);
        _timeSeries = new ProductionTimeSeriesShape(context);
        _seismic = new SeismicStoreShape(context);
        _reservoirManagement = new ReservoirManagementShape(context);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Ddms;

    /// <summary>A record goes before the data its DDMS keeps for it, so a delivery can leave a record without its data until its undo.</summary>
    public bool Undoes => true;

    /// <summary>Where the protocol sends each record: the flow's DDMSs, every registration among them read.</summary>
    public DdmsRouting Routing => _routing;

    /// <summary>The attributes a batched storage read projects to see the links every shape keeps on a record.</summary>
    public static IReadOnlyList<string> LinkAttributes { get; } = ["data.ExtensionProperties", "data.DDMSDatasets"];

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var prepared = await PrepareAsync(work, ct).ConfigureAwait(false);
        return await SendAsync(work, prepared, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything the route checks before its first request, for a record and the data its DDMS keeps for it
    /// (<paramref name="work"/>'s <see cref="DeliveryWork.Payload"/>): where the record goes, and whatever its shape checks.
    /// A record that fails is held here, so a route that sends something else first (files, a manifest) checks it before
    /// that too.
    /// </summary>
    public async Task<PreparedDdmsWork> PrepareAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var paths = Held(work.TargetId);
        var state = await ShapeOf(paths).PrepareAsync(work, paths, ct).ConfigureAwait(false);
        return new PreparedDdmsWork(paths, state);
    }

    /// <summary>
    /// Writes what <paramref name="prepared"/> checked: the record through its collection when the work delivers it, then
    /// the data its DDMS keeps for it. <paramref name="work"/> may differ from the work that was prepared only in what a
    /// composed route learned since (its dataset list, the version its manifest wrote).
    /// </summary>
    public Task<DeliveryOutcome> SendAsync(DeliveryWork work, PreparedDdmsWork prepared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(prepared);
        return ShapeOf(prepared.Paths).SendAsync(work, prepared.Paths, prepared.State, ct);
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
    {
        var paths = _routing.ForRecord(targetId);
        return ShapeOf(paths).VerifyAsync(paths, targetId, expectedVersion, ct);
    }

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
    {
        if (_routing.StorageReadPath(targetId) is { } storage)
        {
            return RecordWriter.ReadAsync(_client, storage, targetId, ct);
        }

        var paths = _routing.ForRecord(targetId);
        return ShapeOf(paths).ReadAsync(paths, targetId, ct);
    }

    /// <summary>
    /// The storage service's version list for a record it keeps (<see cref="StorageVersionsPath"/>): one no DDMS the flow
    /// reaches serves, or a Wellbore DDMS record; null for another DDMS's record, whose versions the storage service does
    /// not hold the whole of.
    /// </summary>
    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => StorageVersionsPath(targetId) is { } storage
            ? RecordWriter.VersionsAsync(_client, storage, targetId, ct)
            : Task.FromResult<IReadOnlyList<long>?>(null);

    /// <summary>A record the storage service keeps (<see cref="StorageVersionsPath"/>), as it held it at <paramref name="version"/>.</summary>
    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => StorageVersionsPath(targetId) is { } storage
            ? RecordWriter.ReadVersionAsync(_client, storage, targetId, version, ct)
            : throw new DeliveryException($"The target keeps no version history for {targetId}, so there is no version {version.ToString(CultureInfo.InvariantCulture)} to read.");

    /// <summary>
    /// Where the storage service's read of the record <paramref name="targetId"/> is, when its versions are the whole of it:
    /// a record no DDMS the flow reaches serves, or a record of a Wellbore DDMS collection, which is a storage record whose
    /// bulk data the version names by its <c>bulkURI</c> (<see cref="WellboreDdmsBulkLink"/>), under a platform endpoint.
    /// Null for any other record: another DDMS keeps data of its own beside the record, which a version does not hold.
    /// </summary>
    private string? StorageVersionsPath(string targetId)
        => _routing.StorageReadPath(targetId) ?? (_routing.PlatformEndpoint && ServedByWellboreDdms(targetId) ? OsduRecordProtocol.DefaultVerifyPath : null);

    /// <summary>Whether a Wellbore DDMS collection the flow reaches serves the record <paramref name="targetId"/>.</summary>
    private bool ServedByWellboreDdms(string targetId)
    {
        try
        {
            return _routing.ForRecord(targetId).Shape == DdmsShape.WellboreDdmsV3;
        }
        catch (DeliveryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes Wellbore DDMS records back as they were at an earlier version, through the storage service rather than the
    /// DDMS (<see cref="RecordRestores"/>): the DDMS refuses a write whose <c>bulkURI</c> is not the one its latest version
    /// holds (<see cref="WellboreDdmsBulkLink"/>), while the version written back through storage carries its own, so the
    /// bulk data of that version is what the DDMS serves again. A record of any other DDMS, or one the storage service is
    /// not reachable for (the endpoint is a DDMS itself), is refused, naming why.
    /// </summary>
    public async Task<IReadOnlyList<RestoreResult>> RestoreBatchAsync(IReadOnlyList<VersionRestore> restores, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restores);
        var results = new RestoreResult?[restores.Count];
        var writable = new List<(int Index, VersionRestore Restore)>();
        for (var i = 0; i < restores.Count; i++)
        {
            var restore = restores[i];
            if (Reversals.ReversalRoute.DdmsRestoreRefusal(_routing, restore.TargetId) is { } refusal)
            {
                results[i] = new RestoreResult(restore, null, null, new DeliveryException(refusal));
            }
            else
            {
                writable.Add((i, restore));
            }
        }

        if (writable.Count > 0)
        {
            // The storage service's own paths under the platform endpoint, with the flow's batching; nothing of the DDMS's
            // paths applies to it.
            var written = await RecordRestores.WriteAsync(StorageWriter(), writable.Select(w => w.Restore).ToList(), ct).ConfigureAwait(false);
            for (var j = 0; j < writable.Count; j++)
            {
                results[writable[j].Index] = written[j];
            }
        }

        return results.Select(r => r!).ToList();
    }

    /// <summary>The storage service's record writer under the platform endpoint, made when a restore first needs it.</summary>
    private OsduRecordProtocol? _storage;

    /// <summary>
    /// The records' access, through the storage service past the DDMS, under a platform endpoint only: a Wellbore DDMS record
    /// is a storage record, and the DDMS reads it under the caller's token before it serves the record's bulk data, so its ACL
    /// governs both (osdu/docs/reference/concepts/access-restriction.md). Which records of the flow that holds for is
    /// <c>RestrictionRoute</c>'s to say. Null when the endpoint is a DDMS itself, where the storage service is not under it.
    /// </summary>
    public IRecordAccess? Access => _routing.PlatformEndpoint
        ? _access ??= new StorageRecordAccess(_client, OsduRecordProtocol.DefaultRecordPath, OsduRecordProtocol.DefaultVerifyBatchPath)
        : null;

    public string? AccessRefusal => _routing.PlatformEndpoint
        ? null
        : "the flow's endpoint is the DDMS itself, so the storage service that keeps the records' ACL is not under it; make the endpoint the platform root and give the DDMS its root under target.ddms";

    private StorageRecordAccess? _access;

    /// <summary>
    /// Undoes what unfinished deliveries left (osdu/docs/reference/concepts/artifacts-and-undo.md, ddms): for each
    /// record, what its DDMS's shape made beside it (a session, content datasets, rows, a dataset and its lock), then
    /// the record itself, removed through the DDMS when the unit created it and given back the version the unit
    /// replaced when it updated it.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var results = new List<UndoResult>();
        foreach (var work in works)
        {
            results.AddRange(await UndoRecordAsync(work, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Undoes one record's units on the record's DDMS: what its shape made beside the record first, then the record itself. A
    /// composed route undoes its own artifacts and hands the DDMS's here. A record the flow can no longer route has every
    /// artifact answered failed, naming why, for the sweep to try again once the flow can.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoRecordAsync(UndoWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        DdmsRecordPaths paths;
        try
        {
            paths = _routing.ForRecord(work.TargetId);
        }
        catch (DeliveryException ex)
        {
            return work.Items.Select(i => UndoResult.Failed(i, $"the record cannot be routed to its DDMS: {HeaderRedaction.RedactMessage(ex.Message)}")).ToList();
        }

        var shape = ShapeOf(paths);
        var record = work.Items.Where(i => ArtifactRoles.IsTheRecord(i.Artifact.Role)).ToList();
        var beside = work.Items.Except(record).ToList();
        var results = new List<UndoResult>(work.Items.Count);
        if (beside.Count > 0)
        {
            results.AddRange(await shape.UndoAsync(work, paths, beside, ct).ConfigureAwait(false));
        }

        // What the delivery made beside the record and could not undo yet is found through the record (a content dataset by its
        // URN) or can still write into it (a session settling): the record waits with it, for the next undo.
        if (record.Count > 0 && !work.KeepRecord && results.Where(r => r.Outcome == ArtifactStatus.Failed).ToList() is { Count: > 0 } waiting)
        {
            results.AddRange(record.Select(i => UndoResult.Failed(i, string.Create(
                CultureInfo.InvariantCulture,
                $"{waiting.Count} item(s) the delivery made beside the record could not be undone yet ({waiting[0].Note}); the record is taken back with them, on the next undo"))));
            return results;
        }

        if (record.Count > 0)
        {
            var side = shape.RecordSide(paths);
            side = paths.Shape == DdmsShape.WellboreDdmsV3
                ? side with
                {
                    // A Wellbore DDMS record goes back through storage with its own bulk link (RestoreBatchAsync), which also
                    // brings back the bulk data that version names.
                    Restorer = Reversals.ReversalRoute.DdmsRestoreRefusal(_routing, work.TargetId) is null ? this : null,
                    RestoreRefusal = Reversals.ReversalRoute.DdmsRestoreRefusal(_routing, work.TargetId),
                    Versions = VersionsAsync,
                }
                : side with
                {
                    // Every other shape's record is a storage record; its metadata goes back through storage, under a platform
                    // endpoint, while what the DDMS keeps beside it is undone by the shape.
                    Restorer = side.Restorer ?? (_routing.PlatformEndpoint ? StorageWriter() : null),
                    RestoreRefusal = side.RestoreRefusal ?? (_routing.PlatformEndpoint ? null : "the flow's endpoint is the DDMS itself, so the storage service that keeps the record's versions is not under it"),
                    Versions = side.Versions ?? (_routing.PlatformEndpoint ? (id, token) => RecordWriter.VersionsAsync(_client, OsduRecordProtocol.DefaultVerifyPath, id, token) : null),
                };
            results.AddRange(await ArtifactUndo.RecordItselfAsync(work, record, side, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>The storage service's record writer under the platform endpoint, with the flow's batching.</summary>
    private OsduRecordProtocol StorageWriter()
        => _storage ??= new OsduRecordProtocol(_client, RecordRestores.WriterOptions(new ProtocolOptions { BatchSize = _options.BatchSize }), _time);

    /// <summary>
    /// Asks each DDMS the flow reaches for its service description, as its shape describes itself (<c>GET /about</c> of
    /// the Wellbore DDMS, <c>GET /info</c> of the others, RAFS's type catalogue, the historian's query service, Seismic
    /// Store's status and the flow's subproject, and the Reservoir Management DDMS's health check and a read behind its
    /// token), or the flow's own probe path. The target is reachable when every one of
    /// them answers.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var probes = _routing.ProbePaths;
        if (probes.Count == 0)
        {
            return new ProbeOutcome(false, 0, "the flow names its own DDMS paths and reaches no DDMS whose service description could be asked; name protocolOptions.probePath", string.Empty);
        }

        var answered = new List<ProbeOutcome>(probes.Count);
        foreach (var probe in probes)
        {
            var path = probe;
            if (path.Contains(DdmsCatalog.PartitionToken, StringComparison.Ordinal))
            {
                // A Seismic Store tenant the flow names by its partition.
                if (_client.Header(Documents.FlowMapper.PartitionHeader) is not { Length: > 0 } partition)
                {
                    return new ProbeOutcome(false, 0, $"the flow names no Seismic Store tenant and sends no {Documents.FlowMapper.PartitionHeader} to take it from", probe);
                }

                path = path.Replace(DdmsCatalog.PartitionToken, UrlPath.EscapeSegment(partition), StringComparison.Ordinal);
            }

            var outcome = await RecordWriter.ProbeAsync(_client, path, ct).ConfigureAwait(false);
            if (!outcome.Reachable)
            {
                return outcome;
            }

            answered.Add(outcome);
        }

        return answered.Count == 1
            ? answered[0]
            : answered[^1] with
            {
                Detail = _routing.Services.Count > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"all {_routing.Services.Count} DDMSs answered")
                    : string.Create(CultureInfo.InvariantCulture, $"the DDMS answered all {answered.Count} probes"),
                Path = string.Join(", ", answered.Select(a => a.Path)),
            };
    }

    private LegalTagValidator? _legal;

    /// <summary>Asks the legal service under this target, when the flow's target reaches it (<see cref="DdmsRouting.LegalValidatePath"/>).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (_routing.LegalValidatePath is not { } path)
        {
            return null;
        }

        _legal ??= new LegalTagValidator(_client, path, _time);
        return await _legal.InvalidAsync(tags, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the record as the shape of its DDMS removes one (see each shape for what each scope calls).</summary>
    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var paths = Held(targetId);
        return ShapeOf(paths).DeleteAsync(paths, targetId, scope, targetState, ct);
    }

    /// <summary>
    /// Carries the link the record <paramref name="targetId"/> keeps to the data its DDMS holds, from its stored version
    /// into <paramref name="document"/>, which rewrites it past the DDMS (a manifest). Returns false when the document
    /// rendered a link of its own that differs from the stored one, which the DDMS's is written over.
    /// </summary>
    public bool CarryLink(string targetId, JsonObject? stored, JsonObject document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(document);
        return ShapeOf(Held(targetId)).CarryLink(stored, document);
    }

    /// <summary>The call pattern of the DDMS a record goes to; paths the flow names are a Wellbore DDMS facade's.</summary>
    private IDdmsShape ShapeOf(DdmsRecordPaths paths) => paths.Shape switch
    {
        DdmsShape.WellboreDdmsV3 => _wellbore,
        DdmsShape.WellDeliveryV1 => _wellDelivery,
        DdmsShape.RafsV2 => _rafs,
        DdmsShape.ProductionTimeSeriesV1 => _timeSeries,
        DdmsShape.SeismicStoreV3 => _seismic,
        DdmsShape.ReservoirManagement => _reservoirManagement,
        _ => throw new InvalidOperationException($"The ddms route has no call pattern for the shape {paths.Shape}."),
    };

    /// <summary>The calls for a record, a record the flow cannot route being held with the reason.</summary>
    private DdmsRecordPaths Held(string targetId)
    {
        try
        {
            return _routing.ForRecord(targetId);
        }
        catch (DeliveryException ex)
        {
            throw new RecordHeldException(ex.Message, ex);
        }
    }

    /// <summary>The request factory is synchronous; opening a blob stream is cheap and the copy is what streams.</summary>
    internal static Stream OpenSync(IPayloadSource payload, PayloadFile chunk)
        => payload.OpenAsync(chunk).GetAwaiter().GetResult();
}

/// <summary>What the ddms route checked for one record before its first request, and what its send uses.</summary>
/// <param name="Paths">Where the record and its bulk data go.</param>
/// <param name="State">What the shape of the record's DDMS checked and its send reads.</param>
public sealed record PreparedDdmsWork(DdmsRecordPaths Paths, object? State);
