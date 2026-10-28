using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>Where one record's Seismic Store dataset is: <c>sd://{tenant}/{subproject}/{folder}/{name}</c>.</summary>
internal sealed record SeismicDataset(string Tenant, string Subproject, string? Folder, string Name)
{
    /// <summary>The folder the dataset is in, as a record's <c>FileCollectionPath</c> names it: <c>sd://tenant/subproject/folder/</c>.</summary>
    public string FolderPath => $"sd://{Tenant}/{Subproject}/{(Folder is null ? string.Empty : Folder + "/")}";

    /// <summary>The dataset's address: <c>sd://tenant/subproject/folder/name</c>.</summary>
    public string SdPath => FolderPath + Name;
}

/// <summary>One object of a dataset: its key under the dataset's location, and the range of which payload file it holds.</summary>
internal sealed record SeismicObject(string Key, int File, long Offset, long Length);

/// <summary>What the Seismic Store shape checked of a record before its first request: its dataset and its files.</summary>
internal sealed record SeismicPlan(SeismicDataset Dataset, IReadOnlyList<PayloadFile> Files, IReadOnlyList<string> Names, long Size);

/// <summary>
/// The Seismic Store v3 shape (osdu/specs/seismic-ddms/INTEGRATION.md, section 9 in particular). A record of a
/// <c>dataset--FileCollection.*</c> type is a dataset: <c>sd://{tenant}/{subproject}/{folder}/{name}</c>, the tenant and
/// subproject and folder being the DDMS's settings and the name the record's key. The record goes to Seismic Store as the
/// dataset's <c>seismicmeta</c>, which the service writes through Storage under the record's own id, pointing at the
/// dataset in the form Seismic Store's v4 resolves (<c>DatasetProperties.FileCollectionPath</c> and a
/// <c>FileSourceInfos</c> entry naming the dataset). Its files are the record's payload.
///
/// <list type="number">
/// <item>A write lock id (<c>W</c> and 32 letters and digits, drawn from the record's delivery key and the dataset's path)
/// is recorded as a step before anything takes it. Every try of every delivery of the record takes the same id, so the
/// service answers a replay as the first call, and a lock an earlier delivery left behind as this one's own.</item>
/// <item>A new dataset is registered (<c>POST /dataset/tenant/{t}/subproject/{s}/dataset/{name}?path=</c>) with the
/// record's first legal tag as <c>ltag</c>; the registration takes the write lock. A dataset that already exists and holds
/// this record is taken over; one holding another record holds the record, and one being deleted is tried again later. A
/// dataset whose files are written and that this delivery did not just register is opened for writing
/// (<c>PUT .../lock?openmode=write</c>), after its read-only flag is lifted where the flow closes datasets read-only.</item>
/// <item>Credentials come from <c>GET /utility/upload-connection-string?sdpath=</c>, once per try, and the files go to the
/// store behind them in the layout the service's clients read: on Azure a single file cut into blobs <c>0</c> to
/// <c>N-1</c>, elsewhere one object <c>0</c>, and the files of a dataset of several under their names
/// (<see cref="SeismicObjectStore"/>). The upload is a step that records how many objects landed, so a later try sends
/// only the rest; objects an earlier delivery left and this one no longer has are removed.</item>
/// <item>The dataset is closed (<c>PATCH ...?close={lock id}</c>) with its file metadata
/// (<c>{type: GENERIC, size, nobjects, md5Checksum}</c>), the read-only flag, and the record when it has not gone yet. A
/// changed record of an unchanged dataset is patched alone, without a lock. A record held after this delivery took the
/// lock releases it, so the dataset does not stay locked for the lock's day.</item>
/// <item>The record's version is read from Storage, which Seismic Store does not return. A record Seismic Store did not
/// write (its Storage writes can be turned off) is written there: one Storage does not hold, and one still at the version
/// the ledger holds after this delivery sent it, since every write is a new version.</item>
/// </list>
///
/// Removal: the reversible scope is Storage's soft delete of the record; the dataset and its files stay, since Seismic
/// Store has no reversible delete. Everything deletes the dataset with its files, then purges the record, except on gc,
/// where one dataset's delete removes every dataset of its subproject (section 6.3), so it is refused. Verification reads
/// the record from Storage and checks Seismic Store still holds its dataset.
/// </summary>
internal sealed class SeismicStoreShape(DdmsShapeContext context) : IDdmsShape
{
    public const string LockStep = "lock";
    public const string RegisterStep = "register";
    public const string UploadStep = "upload";
    public const string CloseStep = "close";

    /// <summary>The step that records the dataset opened for writing, and what that took: its write lock, and its read-only flag lifted.</summary>
    public const string OpenStep = "open";

    // The artifacts of a Seismic Store delivery (osdu/docs/reference/concepts/artifacts-and-undo.md, Seismic
    // Store): the dataset a unit registered, the write lock it holds, and the read-only flag it lifted, each undone
    // in its own way when the delivery does not complete.
    private const string DatasetSlot = "seismic-dataset";
    private const string LockSlot = "lock";
    private const string ReadOnlySlot = "readonly";

    /// <summary>The slot of the record a registration carries, named before it is sent and given way to the record slot once it answers.</summary>
    private const string RegisteredRecordSlot = "record:register";

    /// <summary>The idempotency key register, lock and close take (not in the contract; osdu/specs/seismic-ddms/INTEGRATION.md section 1.3).</summary>
    public const string LockHeader = "x-seismic-dms-lockid";

    /// <summary>The header every Seismic Store answer names its provider in.</summary>
    public const string ProviderHeader = "Service-Provider";

    public const string DatasetKey = "seismicStore.dataset";
    public const string LocationKey = "seismicStore.location";
    public const string ProviderKey = "seismicStore.provider";
    public const string ObjectsKey = "seismicStore.objects";
    public const string LayoutKey = "seismicStore.layout";
    public const string NamesKey = "seismicStore.names";
    public const string SizeKey = "seismicStore.size";
    public const string Md5Key = "seismicStore.md5";

    /// <summary>What the registration step says it did: registered the dataset under the lock, or took over one that existed.</summary>
    public const string TookValue = "took";
    public const string TookLock = "lock";
    public const string TookExisting = "existing";

    /// <summary>A delete Seismic Store has started and not finished marks the dataset's status with this prefix (section 6.3).</summary>
    public const string DeletingStatus = "DELETE:";

    private const int MiB = 1024 * 1024;
    private const int ProgressObjects = 16;
    private const int MaxNamesLength = 16_000;
    private const string LockAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int LockLength = 32;
    private const string ChunkLayout = "chunks";
    private const string FileLayout = "files";

    /// <summary>The objects a dataset this delivery took over held, by its file metadata; a registration step value only.</summary>
    private const string EarlierObjectsValue = "earlierObjects";

    private readonly OsduHttpClient _client = context.Client;
    private readonly ProtocolOptions _options = context.Options;
    private readonly DdmsRouting _routing = context.Routing;
    private readonly ILogger _logger = context.Logger;
    private readonly TimeProvider _time = context.Time;

    public async Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var dataset = DatasetOf(route, work.TargetId);

        // Whatever this delivery does, it may register the dataset again, which sends the record.
        if (RecordProblem(route, work.Document) is { } problem)
        {
            throw new RecordHeldException(problem);
        }

        if (!work.DeliverPayload)
        {
            return new SeismicPlan(dataset, [], [], 0);
        }

        var payload = work.Payload ?? throw new RecordHeldException("the record needs its files but none are attached");
        var files = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new RecordHeldException($"no file was found for the dataset {dataset.SdPath}");
        }

        var names = files.Select(f => FileUploads.FileName(f.Path)).ToList();
        if (files.Count > 1)
        {
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            {
                throw new RecordHeldException($"two files of the dataset {dataset.SdPath} have the same name, and a dataset of several files keeps each under its name");
            }

            // A dot segment would be folded away in the object's address, and a control character cannot be sent in one.
            if (names.FirstOrDefault(n => n.Length == 0 || n is "." or ".." || n.Any(char.IsControl)) is { } bad)
            {
                throw new RecordHeldException($"the file name '{bad}' cannot name an object of the dataset {dataset.SdPath}");
            }
        }

        if (files.FirstOrDefault(f => f.Size < 0) is { } unsized)
        {
            throw new RecordHeldException($"the file {FileUploads.FileName(unsized.Path)} has no known size, which its upload and the dataset's file metadata need");
        }

        return new SeismicPlan(dataset, files, names, files.Sum(f => f.Size));
    }

    public async Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var plan = prepared as SeismicPlan
            ?? throw new InvalidOperationException("The Seismic Store shape was handed work another shape prepared.");
        var attempt = new Attempt(work, route, Settings(route), plan, new DeliverySteps(_time));
        if (_options.PreserveDataKeys.Count > 0 && work.ExistingVersion is not null)
        {
            // Every call that sends the record sends it whole, so the data keys OSDU owns come from the stored record first.
            await RecordWriter.PreserveAsync(_client, route.RecordPath, work.TargetId, attempt.Document, _options.PreserveDataKeys, StepLinks.Of(work), ct).ConfigureAwait(false);
        }

        EnsureLink(attempt.Document, plan.Dataset, work.DeliverPayload ? plan.Size : attempt.Known ? KnownSize(work.TargetState) : null);
        attempt.RecordWritten = Resume(work, OsduDdmsProtocol.MetadataStep, attempt.Steps) is not null;

        if (work.Completed(CloseStep) is { } closed)
        {
            // An earlier try closed the dataset; only the record's version is read again.
            ResumeClosed(attempt, closed);
        }
        else
        {
            try
            {
                await WriteAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (RecordHeldException) when (attempt.HoldsLock)
            {
                await ReleaseAsync(attempt, ct).ConfigureAwait(false);
                throw;
            }
        }

        if (attempt.Location is { } location)
        {
            attempt.Returned[LocationKey] = location;
        }

        if (attempt.Provider is { } provider)
        {
            attempt.Returned[ProviderKey] = ProviderLabel(provider);
        }

        var version = await RecordVersionAsync(attempt, ct).ConfigureAwait(false);
        if (version is { } v)
        {
            attempt.Returned["version"] = v.ToString(CultureInfo.InvariantCulture);
        }

        if (attempt.PayloadDelivered)
        {
            attempt.Details.Insert(0, string.Create(
                CultureInfo.InvariantCulture,
                $"{attempt.Returned.GetValueOrDefault(ObjectsKey, "0")} object(s) of {attempt.Returned.GetValueOrDefault(SizeKey, "0")} bytes in {plan.Dataset.SdPath}, {attempt.Sent} sent by this try"));
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = work.DeliverMetadata,
            PayloadDelivered = attempt.PayloadDelivered,
            TargetVersion = version ?? work.ExistingVersion,
            ChunksSent = attempt.Sent,
            Detail = attempt.Details.Count == 0 ? null : string.Join("; ", attempt.Details),
            Returned = attempt.Returned,
            Steps = attempt.Steps.Steps,
        };
    }

    /// <summary>
    /// Reads the record from Storage and, when Storage holds it, checks Seismic Store still holds its dataset for it: a
    /// dataset that is gone, holds another record or is being deleted is drift, which a reconciling verify redelivers.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var record = await RecordWriter.VerifyAsync(_client, route.RecordPath, targetId, expectedVersion, ct).ConfigureAwait(false);
        if (record.Outcome is VerifyOutcome.Missing or VerifyOutcome.Error)
        {
            return record;
        }

        SeismicDataset dataset;
        try
        {
            dataset = DatasetOf(route, targetId);
        }
        catch (RecordHeldException ex)
        {
            return new VerifyResult(VerifyOutcome.Error, record.ObservedVersion, ex.Message);
        }

        var found = await GetDatasetAsync(route, dataset, ct).ConfigureAwait(false);
        if (DatasetProblem(dataset, found, targetId) is not { } problem)
        {
            return record;
        }

        return new VerifyResult(VerifyOutcome.Drifted, record.ObservedVersion, record.Outcome == VerifyOutcome.Drifted && record.Detail is { Length: > 0 } detail ? $"{detail}; {problem}" : problem);
    }

    /// <summary>What is wrong with the dataset of a delivered record, as Seismic Store answered for it, or null.</summary>
    private static string? DatasetProblem(SeismicDataset dataset, JsonElement? found, string targetId)
    {
        if (found is not { } stored)
        {
            return $"Seismic Store no longer holds the dataset {dataset.SdPath}";
        }

        if (Text(stored, "status") is { } status && status.StartsWith(DeletingStatus, StringComparison.Ordinal))
        {
            return $"Seismic Store is deleting the dataset {dataset.SdPath} (status {status})";
        }

        var holder = Text(stored, "seismicmeta_guid");
        return string.Equals(holder, targetId, StringComparison.Ordinal)
            ? null
            : $"the dataset {dataset.SdPath} holds {holder ?? "no record"} rather than this record";
    }

    public Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct)
        => RecordWriter.ReadAsync(_client, RouteOf(paths).RecordPath, targetId, ct);

    /// <summary>
    /// The reversible scope soft-deletes the record in Storage and leaves the dataset, which Seismic Store cannot delete
    /// reversibly; the history scope purges the record's earlier versions. Everything deletes the dataset and its files
    /// (<c>DELETE /dataset/...</c>), then purges the record, which the dataset's delete leaves (section 6.3); it is refused on
    /// gc, where one dataset's delete removes the files of every dataset in the subproject.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var settings = Settings(route);
        var storagePath = scope switch
        {
            RemovalScope.Record => _routing.StorageDeletePath,
            RemovalScope.History => _routing.HistoryPath,
            _ => _routing.StoragePurgePath,
        } ?? throw new RecordHeldException(
            $"{DdmsRouting.Describe(route.Service)} keeps its records in Storage, and this flow does not say where the storage service is. Give the DDMS its root under target.ddms, "
            + "the flow's endpoint being the OSDU platform root.");
        var dataset = DatasetOf(route, targetId);
        if (scope != RemovalScope.Everything)
        {
            var outcome = await RecordWriter.DeleteAsync(_client, new RemovalPaths(storagePath, storagePath, storagePath), targetId, scope, ct).ConfigureAwait(false);
            return scope == RemovalScope.Record
                ? outcome with { Detail = outcome.Detail + $"; the dataset {dataset.SdPath} and its files stay in Seismic Store, which has no reversible delete" }
                : outcome;
        }

        var provider = settings.Provider ?? ProviderOf(targetState?.GetValueOrDefault(ProviderKey)) ?? await ServiceProviderAsync(route, ct).ConfigureAwait(false);
        if (provider == DdmsProvider.Gc)
        {
            throw new RecordHeldException(
                $"Seismic Store on gc deletes the files of every dataset in a subproject when one dataset is deleted (osdu/specs/seismic-ddms/INTEGRATION.md section 6.3), so {dataset.SdPath} "
                + "is not deleted here; remove the record, and delete the dataset by hand once that behaviour is checked on a disposable subproject");
        }

        await _client.SendJsonAsync(HttpMethod.Delete, DatasetUrl(route, dataset, string.Empty), null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        var purged = await RecordWriter.DeleteAsync(_client, new RemovalPaths(storagePath, storagePath, storagePath), targetId, RemovalScope.Everything, ct).ConfigureAwait(false);
        return purged with { Deleted = true, AlreadyGone = false, Detail = $"the dataset {dataset.SdPath} and its files deleted from Seismic Store; the record {(purged.AlreadyGone ? "was already gone" : "purged from OSDU")}" };
    }

    /// <summary>
    /// Undoes what unfinished deliveries left in Seismic Store: a dataset a unit registered is deleted with its files and its
    /// lock (not on gc, where one dataset's delete takes the files of every dataset in its subproject, and not while newer work
    /// of the record will take it over); a lock a unit holds is released; a read-only flag it lifted is set again. Objects an
    /// update overwrote cannot be put back: Seismic Store keeps no earlier objects, and the ledger no earlier bytes.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(UndoWork work, DdmsRecordPaths paths, IReadOnlyList<UndoItem> items, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var results = new List<UndoResult>(items.Count);
        SeismicDataset dataset;
        try
        {
            dataset = DatasetOf(route, work.TargetId);
        }
        catch (RecordHeldException ex)
        {
            return items.Select(i => UndoResult.Kept(i, ex.Message)).ToList();
        }

        var deleted = false;
        foreach (var item in items.Where(i => i.Artifact.Slot == DatasetSlot))
        {
            if (work.KeepRecord)
            {
                results.Add(UndoResult.Superseded(item, $"the record's newer work takes {dataset.SdPath} over, so it is left as it is"));
                continue;
            }

            try
            {
                var provider = Settings(route).Provider ?? ProviderOf(work.TargetState.GetValueOrDefault(ProviderKey)) ?? await ServiceProviderAsync(route, ct).ConfigureAwait(false);
                if (provider == DdmsProvider.Gc)
                {
                    results.Add(UndoResult.Kept(item, $"Seismic Store on gc deletes the files of every dataset in a subproject when one dataset is deleted, so {dataset.SdPath}, which the unfinished delivery registered, is left with its files"));
                    continue;
                }

                var result = await _client.SendJsonAsync(HttpMethod.Delete, DatasetUrl(route, dataset, string.Empty), null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
                deleted = true;
                results.Add((int)result.Status == 404
                    ? UndoResult.Gone(item, $"Seismic Store no longer holds {dataset.SdPath}")
                    : UndoResult.Removed(item, $"{dataset.SdPath} deleted with its files and its lock (Seismic Store has no reversible delete)"));
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, ArtifactUndo.Redact(ex)));
            }
        }

        // The lock goes before the read-only flag is set again: a dataset is made read-only once no write lock of the delivery holds it.
        foreach (var item in items.Where(i => i.Artifact.Slot != DatasetSlot).OrderBy(i => i.Artifact.Slot == ReadOnlySlot ? 1 : 0))
        {
            if (item.Artifact.Role != ArtifactRoles.Lock)
            {
                results.Add(UndoResult.Kept(item, "the Seismic Store shape makes nothing of this kind beside a record"));
                continue;
            }

            if (deleted)
            {
                results.Add(UndoResult.Gone(item, $"{dataset.SdPath} was deleted, and its lock and flags with it"));
                continue;
            }

            try
            {
                if (item.Artifact.Slot == ReadOnlySlot)
                {
                    var result = await _client.SendJsonAsync(HttpMethod.Patch, DatasetUrl(route, dataset, string.Empty), new JsonObject { ["readonly"] = true }, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
                    results.Add((int)result.Status == 404
                        ? UndoResult.Gone(item, $"Seismic Store no longer holds {dataset.SdPath}")
                        : UndoResult.Removed(item, $"{dataset.SdPath} read-only again"));
                }
                else if (item.Artifact.Locator is not { } lockId)
                {
                    results.Add(UndoResult.Kept(item, $"the lock on {dataset.SdPath} was recorded without its id, so it is left to lapse rather than release whoever's lock the dataset holds"));
                }
                else
                {
                    results.Add(await ReleaseLockAsync(route, dataset, lockId, ct).ConfigureAwait(false)
                        ? UndoResult.Removed(item, $"the write lock on {dataset.SdPath} released")
                        : UndoResult.Gone(item, $"Seismic Store holds no lock on {dataset.SdPath} under this delivery's id: it lapsed, and any lock the dataset holds now is another writer's, left as it is"));
                }
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, ArtifactUndo.Redact(ex)));
            }
        }

        return results;
    }

    /// <summary>The record is a Storage record: its reversible removal and its read are storage's.</summary>
    public RecordSide RecordSide(DdmsRecordPaths paths) => new()
    {
        Remove = _routing.StorageDeletePath is { } delete
            ? (id, token) => RecordWriter.DeleteAsync(_client, new RemovalPaths(delete, delete, delete), id, RemovalScope.Record, token)
            : null,
        RemoveRefusal = "the record is a Storage record, and this flow does not say where the storage service is; give the DDMS its root under target.ddms, the flow's endpoint being the OSDU platform root",
        Read = (id, token) => RecordWriter.ReadAsync(_client, RouteOf(paths).RecordPath, id, token),
    };

    /// <summary>
    /// Points <paramref name="document"/> at its dataset (<c>DatasetProperties.FileCollectionPath</c> and one
    /// <c>FileSourceInfos</c> entry naming the dataset). Returns false when the document pointed elsewhere.
    /// </summary>
    public bool CarryLink(JsonObject? stored, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if ((Text(document["id"]) ?? Text(stored?["id"])) is not { } id)
        {
            return true;
        }

        var route = _routing.ForRecord(id).Route ?? throw new InvalidOperationException($"{id} has no Seismic Store to go to.");
        return EnsureLink(document, DatasetOf(route, id), null);
    }

    /// <summary>
    /// Why Seismic Store would not take <paramref name="document"/> as the <c>seismicmeta</c> of a dataset of
    /// <paramref name="route"/>'s entity type, or null: a kind of four parts naming that type, a data object, owners and
    /// viewers, and at least one legal tag and country (without which the service writes <c>US</c>).
    /// </summary>
    internal static string? RecordProblem(DdmsRoute route, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(document);
        var kind = Text(document["kind"]);
        var parts = kind?.Split(':') ?? [];
        if (kind is null || parts.Length != 4 || !string.Equals(parts[2], route.Collection.EntityType, StringComparison.OrdinalIgnoreCase))
        {
            return $"the kind '{kind}' is not a kind of {route.Collection.EntityType} (<authority>:<source>:{route.Collection.EntityType}:<version>), which {DdmsRouting.Describe(route.Service)} registers these records as";
        }

        if (document["data"] is not JsonObject)
        {
            return "the record has no data object, which Seismic Store requires of a dataset's record";
        }

        if (document["acl"] is not JsonObject acl || !NonEmpty(acl["owners"]) || !NonEmpty(acl["viewers"]))
        {
            return "acl must name owners and viewers; Seismic Store would otherwise give the record the partition's default groups";
        }

        if (document["legal"] is not JsonObject legal || !NonEmpty(legal["legaltags"]) || !NonEmpty(legal["otherRelevantDataCountries"]))
        {
            return "legal must name at least one legal tag and one country; Seismic Store would otherwise write US as the country";
        }

        return null;
    }

    /// <summary>Points a document at its dataset; false when it pointed elsewhere.</summary>
    internal static bool EnsureLink(JsonObject document, SeismicDataset dataset, long? size)
    {
        if (document["data"] is not JsonObject data)
        {
            if (document["data"] is not null)
            {
                return true;
            }

            data = new JsonObject();
            document["data"] = data;
        }

        if (data["DatasetProperties"] is not JsonObject properties)
        {
            if (data["DatasetProperties"] is not null)
            {
                return true;
            }

            properties = new JsonObject();
            data["DatasetProperties"] = properties;
        }

        var agreed = Text(properties["FileCollectionPath"]) is not { } rendered || string.Equals(rendered, dataset.FolderPath, StringComparison.Ordinal);
        properties["FileCollectionPath"] = dataset.FolderPath;

        var infos = properties["FileSourceInfos"] as JsonArray;
        var entry = infos is { Count: > 0 } && infos[0] is JsonObject first ? (JsonObject)first.DeepClone() : new JsonObject();
        agreed &= infos is null or { Count: <= 1 };
        agreed &= Text(entry["FileSource"]) is not { } source || string.Equals(source, dataset.Name, StringComparison.Ordinal);
        entry["FileSource"] = dataset.Name;
        if (size is { } bytes)
        {
            entry["FileSize"] = DatasetService.Size(bytes);
            data["TotalSize"] = DatasetService.Size(bytes);
        }

        properties["FileSourceInfos"] = new JsonArray(entry);
        return agreed;
    }

    /// <summary>The provider a <c>Service-Provider</c> label names, or null.</summary>
    internal static DdmsProvider? ProviderOf(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "azure" => DdmsProvider.Azure,
        "gc" or "google" or "gcp" => DdmsProvider.Gc,
        "anthos" => DdmsProvider.Anthos,
        "ibm" => DdmsProvider.Ibm,
        "aws" => DdmsProvider.Aws,
        _ => null,
    };

    /// <summary>
    /// The objects a dataset's files become (osdu/specs/seismic-ddms/INTEGRATION.md section 4.4): on Azure a single file cut
    /// into blobs <c>0</c> to <c>N-1</c> of the chunk size, elsewhere a single file as one object <c>0</c>, and the files of a
    /// dataset of several under their names.
    /// </summary>
    internal static IReadOnlyList<SeismicObject> Layout(SeismicPlan plan, DdmsProvider provider, long chunkBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Files.Count != 1)
        {
            return plan.Files.Select((f, i) => new SeismicObject(plan.Names[i], i, 0, f.Size)).ToList();
        }

        var size = plan.Files[0].Size;
        if (provider != DdmsProvider.Azure || chunkBytes <= 0 || size <= chunkBytes)
        {
            return [new SeismicObject("0", 0, 0, size)];
        }

        var objects = new List<SeismicObject>();
        for (long offset = 0, key = 0; offset < size; offset += chunkBytes, key++)
        {
            objects.Add(new SeismicObject(key.ToString(CultureInfo.InvariantCulture), 0, offset, Math.Min(chunkBytes, size - offset)));
        }

        return objects;
    }

    /// <summary>
    /// The lock id of a record's dataset: <c>W</c> and 32 letters and digits drawn from the record's delivery key and the
    /// dataset's path. Every try of every delivery of the record takes the same id, so a lock an earlier delivery left (one
    /// that failed after it registered or opened the dataset) is answered as this delivery's own; another flow delivering
    /// to the same dataset has another key, and meets the lock as another writer's.
    /// </summary>
    internal static string LockIdOf(DeliveryKey key, SeismicDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"seismic-store-lock\n{key}\n{dataset.SdPath}"));
        return string.Create(LockLength + 1, hash, static (span, bytes) =>
        {
            span[0] = 'W';
            for (var i = 0; i < LockLength; i++)
            {
                span[i + 1] = LockAlphabet[bytes[i] % LockAlphabet.Length];
            }
        });
    }

    /// <summary>What one try of a delivery knows as it goes.</summary>
    private sealed class Attempt(DeliveryWork work, DdmsRoute route, SeismicStoreSettings settings, SeismicPlan plan, DeliverySteps steps)
    {
        public DeliveryWork Work { get; } = work;

        public DdmsRoute Route { get; } = route;

        public SeismicStoreSettings Settings { get; } = settings;

        public SeismicPlan Plan { get; } = plan;

        public SeismicDataset Dataset => Plan.Dataset;

        public DeliverySteps Steps { get; } = steps;

        /// <summary>The record as this delivery sends it, pointing at its dataset.</summary>
        public JsonObject Document { get; } = (JsonObject)work.Document.DeepClone();

        public Dictionary<string, string> Returned { get; } = new(StringComparer.Ordinal) { ["recordId"] = work.TargetId, [DatasetKey] = plan.Dataset.SdPath };

        public List<string> Details { get; } = [];

        /// <summary>Whether the ledger knows the dataset from an earlier delivery of the record, at the same path.</summary>
        public bool Known { get; set; } = KnowsDataset(work, plan);

        /// <summary>Where the dataset keeps its files, as far as this try knows.</summary>
        public string? Location { get; set; } = KnowsDataset(work, plan) ? work.TargetState.GetValueOrDefault(LocationKey) : null;

        public DdmsProvider? Provider { get; set; } = settings.Provider ?? ProviderOf(work.TargetState.GetValueOrDefault(ProviderKey));

        /// <summary>Whether this delivery wrote the record through Seismic Store, in this try or an earlier one.</summary>
        public bool RecordWritten { get; set; }

        public string? LockId { get; set; }

        /// <summary>Whether this try holds the dataset's write lock: it registered or opened the dataset and has not closed it.</summary>
        public bool HoldsLock { get; set; }

        /// <summary>Whether this try lifted the dataset's read-only flag to open it, which the close sets again.</summary>
        public bool ReadOnlyLifted { get; set; }

        /// <summary>Whether an earlier try of the unit sent a registration it never heard back from.</summary>
        public bool RegisteredBefore { get; set; }

        public int Sent { get; set; }

        public bool PayloadDelivered { get; set; }

        private static bool KnowsDataset(DeliveryWork work, SeismicPlan plan)
            => work.TargetState.TryGetValue(DatasetKey, out var path) && string.Equals(path, plan.Dataset.SdPath, StringComparison.Ordinal);
    }

    /// <summary>The store a try's objects go to, opened (credentials and all) the first time it is needed and once only.</summary>
    private sealed class StoreOnDemand(Func<CancellationToken, Task<SeismicObjectStore>> open)
    {
        private SeismicObjectStore? _store;

        public async Task<SeismicObjectStore> GetAsync(CancellationToken ct) => _store ??= await open(ct).ConfigureAwait(false);
    }

    /// <summary>What the upload wrote: its step values, the keys of the dataset's objects, and how many this try sent.</summary>
    private sealed record Upload(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Keys, int Sent);

    /// <summary>The values of the steps an earlier try completed before it closed the dataset.</summary>
    private static void ResumeClosed(Attempt attempt, IReadOnlyDictionary<string, string> closed)
    {
        Resume(attempt.Work, LockStep, attempt.Steps);
        if (Resume(attempt.Work, RegisterStep, attempt.Steps) is { } registration)
        {
            Merge(attempt.Returned, registration);
            attempt.Location = Value(registration, LocationKey) ?? attempt.Location;
            attempt.Provider ??= ProviderOf(registration.GetValueOrDefault(ProviderKey));
        }

        if (Resume(attempt.Work, UploadStep, attempt.Steps) is { } upload)
        {
            Merge(attempt.Returned, upload);
        }

        // The steps are stored in their order, so the close's values go last.
        attempt.Steps.Resumed(CloseStep, closed);
        Merge(attempt.Returned, closed);
        attempt.PayloadDelivered = attempt.Work.DeliverPayload;
    }

    /// <summary>Registers or opens the dataset, writes its files, and closes it; or patches the record of a dataset whose files stay.</summary>
    private async Task WriteAsync(Attempt attempt, CancellationToken ct)
    {
        var work = attempt.Work;

        // A lock id is taken when the dataset may be registered or its files written, or an earlier try took one.
        if (!attempt.Known || work.DeliverPayload || work.Completed(LockStep) is not null)
        {
            attempt.LockId = await LockIdAsync(attempt, ct).ConfigureAwait(false);
        }

        var registration = Resume(work, RegisterStep, attempt.Steps);
        if (registration is null && !attempt.Known)
        {
            registration = await RegisterAsync(attempt, ct).ConfigureAwait(false);
            attempt.HoldsLock = Took(registration) == TookLock;
        }

        if (registration is not null)
        {
            await AdoptAsync(attempt, registration, ct).ConfigureAwait(false);
        }

        if (work.DeliverPayload)
        {
            await WriteFilesAsync(attempt, registration, ct).ConfigureAwait(false);
        }
        else if (Took(registration) == TookLock)
        {
            // A dataset this delivery registered holds no files yet, and is closed saying so.
            var body = new JsonObject
            {
                ["filemetadata"] = new JsonObject { ["type"] = "GENERIC", ["size"] = 0, ["nobjects"] = 0 },
                ["readonly"] = attempt.Settings.ReadOnly,
            };
            await CloseAsync(attempt, body, ct).ConfigureAwait(false);
            attempt.Returned[ObjectsKey] = "0";
            attempt.Returned[SizeKey] = "0";
        }
        else if (work.DeliverMetadata && !attempt.RecordWritten)
        {
            await RecordIntentAsync(attempt, ct).ConfigureAwait(false);
            await PatchAsync(attempt, new JsonObject { ["seismicmeta"] = attempt.Document.DeepClone() }, ct).ConfigureAwait(false);
            await RecordWrittenAsync(attempt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>What a registration said: where the dataset keeps its files, the provider, and whether it wrote the record.</summary>
    private static async Task AdoptAsync(Attempt attempt, IReadOnlyDictionary<string, string> registration, CancellationToken ct)
    {
        Merge(attempt.Returned, registration);
        attempt.Location = Value(registration, LocationKey) ?? attempt.Location;
        attempt.Provider ??= ProviderOf(registration.GetValueOrDefault(ProviderKey));
        if (Took(registration) == TookLock && !attempt.RecordWritten)
        {
            await RecordWrittenAsync(attempt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Opens the dataset for writing unless this try registered it, uploads its files, removes stale objects, and closes it.</summary>
    private async Task WriteFilesAsync(Attempt attempt, IReadOnlyDictionary<string, string>? registration, CancellationToken ct)
    {
        var work = attempt.Work;
        if (!attempt.HoldsLock)
        {
            // The dataset exists (the ledger knows it, or it held this record already), or an earlier try registered it and
            // its lock may have lapsed: it is opened for writing under the delivery's lock id, which a lock the delivery
            // holds already is answered as.
            var opened = await OpenAsync(attempt, ct).ConfigureAwait(false);
            if (opened is null)
            {
                // The dataset is gone: it is registered again, at a new location, and the record goes with it again.
                attempt.Known = false;
                attempt.RecordWritten = false;
                registration = await RegisterAsync(attempt, ct).ConfigureAwait(false);
                await AdoptAsync(attempt, registration, ct).ConfigureAwait(false);
                if (Took(registration) == TookLock)
                {
                    attempt.HoldsLock = true;
                }
                else
                {
                    opened = await OpenAsync(attempt, ct).ConfigureAwait(false)
                        ?? throw new DeliveryException($"Seismic Store answered that {attempt.Dataset.SdPath} exists, then that it does not; the next try registers it again.");
                }
            }

            if (opened is { } open)
            {
                attempt.HoldsLock = true;
                attempt.Location = open.Location ?? attempt.Location;
                attempt.Provider ??= ProviderOf(open.Provider);
            }
        }

        var location = attempt.Location
            ?? throw new DeliveryException($"Seismic Store did not say where the dataset {attempt.Dataset.SdPath} keeps its files.");
        var provider = attempt.Provider ??= await ServiceProviderAsync(attempt.Route, ct).ConfigureAwait(false);
        CheckProvider(attempt, provider);

        var store = new StoreOnDemand(token => StoreAsync(attempt, provider, location, token));
        var upload = await UploadAsync(attempt, provider, location, store, ct).ConfigureAwait(false);
        attempt.Sent = upload.Sent;
        Merge(attempt.Returned, upload.Values);

        var stale = EarlierKeys(attempt, registration, provider, location).Except(upload.Keys, StringComparer.Ordinal).ToList();
        if (stale.Count > 0)
        {
            var objects = await store.GetAsync(ct).ConfigureAwait(false);
            foreach (var key in stale)
            {
                await objects.DeleteAsync(key, ct).ConfigureAwait(false);
            }

            attempt.Details.Add(string.Create(CultureInfo.InvariantCulture, $"{stale.Count} object(s) an earlier delivery left removed"));
        }

        var body = new JsonObject
        {
            ["filemetadata"] = FileMetadata(upload),
            ["readonly"] = attempt.Settings.ReadOnly,
        };
        var carriesRecord = work.DeliverMetadata && !attempt.RecordWritten;
        if (carriesRecord)
        {
            body["seismicmeta"] = attempt.Document.DeepClone();
            await RecordIntentAsync(attempt, ct).ConfigureAwait(false);
        }

        await CloseAsync(attempt, body, ct).ConfigureAwait(false);
        if (carriesRecord)
        {
            await RecordWrittenAsync(attempt, ct).ConfigureAwait(false);
        }

        attempt.PayloadDelivered = true;
    }

    /// <summary>
    /// The keys of the objects an earlier delivery left at <paramref name="location"/>: those the ledger recorded for the
    /// dataset, or, for a dataset this delivery took over from a delivery the ledger lost, the blobs its file metadata
    /// counts on Azure, the one provider a single file has more than one object on.
    /// </summary>
    private static IEnumerable<string> EarlierKeys(Attempt attempt, IReadOnlyDictionary<string, string>? registration, DdmsProvider provider, string location)
    {
        var state = attempt.Work.TargetState;
        if (attempt.Known && string.Equals(state.GetValueOrDefault(LocationKey), location, StringComparison.Ordinal))
        {
            return state.GetValueOrDefault(LayoutKey) switch
            {
                ChunkLayout when int.TryParse(state.GetValueOrDefault(ObjectsKey), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    => Enumerable.Range(0, count).Select(i => i.ToString(CultureInfo.InvariantCulture)),
                FileLayout when state.GetValueOrDefault(NamesKey) is { Length: > 0 } names => names.Split('/'),
                _ => [],
            };
        }

        if (registration is not null
            && Took(registration) == TookExisting
            && provider == DdmsProvider.Azure
            && attempt.Plan.Files.Count == 1
            && string.Equals(Value(registration, LocationKey), location, StringComparison.Ordinal)
            && int.TryParse(registration.GetValueOrDefault(EarlierObjectsValue), NumberStyles.None, CultureInfo.InvariantCulture, out var earlier))
        {
            return Enumerable.Range(0, earlier).Select(i => i.ToString(CultureInfo.InvariantCulture));
        }

        return [];
    }

    /// <summary>The lock id every call of this delivery takes, recorded before its first use.</summary>
    private static async Task<string> LockIdAsync(Attempt attempt, CancellationToken ct)
    {
        var work = attempt.Work;
        if (work.Completed(LockStep) is { } known && known.TryGetValue("lockId", out var recorded) && IsLockId(recorded))
        {
            attempt.Steps.Resumed(LockStep, known);
            return recorded;
        }

        var id = LockIdOf(work.Key, attempt.Dataset);
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["lockId"] = id };
        var started = attempt.Steps.Now;
        await work.ReportStepAsync(LockStep, values, ct).ConfigureAwait(false);
        attempt.Steps.Add(LockStep, started, null, values);
        return id;
    }

    /// <summary>
    /// Registers the dataset with the record as its <c>seismicmeta</c>, under the lock id. A replay the service answers
    /// with an empty body (a lock an earlier registration kept without saving the dataset) is unlocked and registered
    /// again; a dataset that already exists is taken over when it holds this record.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> RegisterAsync(Attempt attempt, CancellationToken ct)
    {
        var dataset = attempt.Dataset;
        var started = attempt.Steps.Now;
        var url = DatasetUrl(attempt.Route, dataset, string.Empty);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LockHeader] = attempt.LockId ?? throw new InvalidOperationException("A dataset is registered under a lock id."),
            ["ltag"] = FirstLegalTag(attempt.Document),
        };
        var body = new JsonObject { ["seismicmeta"] = attempt.Document.DeepClone() };

        // Every id the registration makes is the route's own (the dataset's path, the lock's id, the record it carries), so each
        // is named before the request: a registration whose answer is lost after it landed is undone all the same. An earlier
        // try of the unit that named them too sent a registration whose answer it never had.
        attempt.RegisteredBefore = attempt.Work.Completed(RegisterStep + "-intent") is not null;
        await attempt.Work.ReportStepAsync(
            RegisterStep + "-intent",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["dataset"] = dataset.SdPath, ["lockId"] = headers[LockHeader] },
            [
                TargetArtifact.Intent(DatasetSlot, ArtifactRoles.Objects, null, dataset.SdPath),
                TargetArtifact.Intent(LockSlot, ArtifactRoles.Lock, headers[LockHeader], dataset.SdPath),
                TargetArtifact.RecordWritten(attempt.Work.TargetId, null, attempt.Work.ExistingVersion) with { Slot = RegisteredRecordSlot, Status = ArtifactStatus.Intent },
            ],
            ct).ConfigureAwait(false);
        for (var round = 1; round <= 2; round++)
        {
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 409, 423 }, ct, idempotent: true, headers: headers).ConfigureAwait(false);
            switch ((int)result.Status)
            {
                case 423:
                    await WithdrawRegistrationAsync(attempt, "the registration answered that another writer holds the dataset's lock; it made nothing", ct).ConfigureAwait(false);
                    throw new DeliveryException(
                        $"Seismic Store keeps {dataset.SdPath} locked for another writer ({HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}); the next try registers it again once the lock is released or has expired.");
                case 409:
                    {
                        if (await GetDatasetAsync(attempt.Route, dataset, ct).ConfigureAwait(false) is not { } found)
                        {
                            // The dataset went between the two calls; it is registered again.
                            continue;
                        }

                        CheckNotDeleting(dataset, found);
                        var holder = Text(found, "seismicmeta_guid");
                        if (!string.Equals(holder, attempt.Work.TargetId, StringComparison.Ordinal))
                        {
                            await WithdrawRegistrationAsync(attempt, $"the dataset exists and belongs to {(holder is null ? "no record" : holder)}, not to this delivery", ct).ConfigureAwait(false);
                            throw new RecordHeldException(
                                $"the dataset {dataset.SdPath} already exists and belongs to {(holder is null ? "no record" : holder)}; the dataset's name is the record key, so {attempt.Work.TargetId} cannot take it");
                        }

                        return await RecordRegistrationAsync(attempt, started, 409, found, result, TookExisting, ct).ConfigureAwait(false);
                    }

                default:
                    {
                        var answered = OsduHttpClient.ParseJson(result, url);
                        if (Text(answered, "name") is not null && Text(answered, "gcsurl") is not null)
                        {
                            return await RecordRegistrationAsync(attempt, started, (int)result.Status, answered, result, TookLock, ct).ConfigureAwait(false);
                        }

                        _logger.LogWarning("Seismic Store answered the registration of {Dataset} without the dataset; its lock is released and the registration repeated.", dataset.SdPath);
                        await _client.SendJsonAsync(HttpMethod.Put, DatasetUrl(attempt.Route, dataset, "/unlock"), null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
                        break;
                    }
            }
        }

        throw new DeliveryException($"Seismic Store answered two registrations of {dataset.SdPath} without the dataset it registered; the next try registers it again.");
    }

    /// <summary>
    /// Settles what a registration named before it went, once its answer says it made none of it: the dataset and the record it
    /// carried, so no undo deletes a dataset that is not this delivery's. The lock stays named: an undo releases only a lock held
    /// under this delivery's id, which another writer's is not.
    /// </summary>
    private static Task WithdrawRegistrationAsync(Attempt attempt, string why, CancellationToken ct)
        => attempt.Work.ReportStepAsync(
            RegisterStep + "-refused",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["dataset"] = attempt.Dataset.SdPath },
            [
                TargetArtifact.Created(DatasetSlot, ArtifactRoles.Objects, attempt.Dataset.SdPath) with { Status = ArtifactStatus.Gone, Note = why },
                TargetArtifact.RecordWritten(attempt.Work.TargetId, null, attempt.Work.ExistingVersion) with { Slot = RegisteredRecordSlot, Status = ArtifactStatus.Gone, Note = why },
            ],
            ct);

    private static async Task<IReadOnlyDictionary<string, string>> RecordRegistrationAsync(
        Attempt attempt, DateTime started, int status, JsonElement answered, HttpFetchResult result, string took, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { [TookValue] = took };
        foreach (var (name, key) in new[]
        {
            ("gcsurl", LocationKey), ("ctag", "seismicStore.ctag"), ("created_by", "seismicStore.createdBy"), ("seismicmeta_guid", "seismicStore.record"),
            ("access_policy", "seismicStore.accessPolicy"),
        })
        {
            if (Text(answered, name) is { } value)
            {
                values[key] = value;
            }
        }

        if (Header(result, ProviderHeader) is { } label)
        {
            values[ProviderKey] = label;
        }

        if (took == TookExisting && Objects(answered) is { } earlier)
        {
            values[EarlierObjectsValue] = earlier.ToString(CultureInfo.InvariantCulture);
        }

        attempt.Steps.Add(RegisterStep, started, status, values);

        // A dataset this delivery registered is the unit's until the delivery completes, with the write lock the registration
        // took, and so is one it takes over that an earlier try of the unit registered without hearing back. One that held the
        // record before the unit began is not this unit's to delete. Either way the registration has answered, so the intent of
        // the record it carried gives way: the record slot names what the delivery writes of it.
        var settled = TargetArtifact.RecordWritten(attempt.Work.TargetId, null, attempt.Work.ExistingVersion) with
        {
            Slot = RegisteredRecordSlot,
            Status = ArtifactStatus.Gone,
            Note = took == TookLock ? "the registration answered; the record slot names the record it wrote" : "the registration answered that the dataset exists; it wrote no record",
        };
        var dataset = TargetArtifact.Created(DatasetSlot, ArtifactRoles.Objects, attempt.Dataset.SdPath, locator: values.GetValueOrDefault(LocationKey));
        IReadOnlyList<TargetArtifact> made = took == TookLock
            ? [dataset, TargetArtifact.Created(LockSlot, ArtifactRoles.Lock, attempt.Dataset.SdPath, locator: attempt.LockId), settled]
            : attempt.RegisteredBefore
                ? [dataset with { Note = "registered by an earlier try of this delivery whose answer was lost, and taken over" }, settled]
                : [dataset with { Status = ArtifactStatus.Gone, Note = "taken over: the dataset held this record before this delivery, so it is not this delivery's to delete" }, settled];
        await attempt.Work.ReportStepAsync(RegisterStep, values, made, ct).ConfigureAwait(false);
        return values;
    }

    /// <summary>
    /// Opens the dataset for writing under the lock id, lifting its read-only flag first where the flow closes datasets
    /// read-only. Null when the dataset is gone.
    /// </summary>
    private async Task<(string? Location, string? Provider)?> OpenAsync(Attempt attempt, CancellationToken ct)
    {
        var dataset = attempt.Dataset;
        if (attempt.Settings.ReadOnly)
        {
            var reopened = await _client.SendJsonAsync(
                HttpMethod.Patch, DatasetUrl(attempt.Route, dataset, string.Empty), new JsonObject { ["readonly"] = false }, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
            if ((int)reopened.Status == 404)
            {
                return null;
            }

            // The flag is lifted from here, whatever the lock's request comes to: the unit sets it again if it does not finish.
            attempt.ReadOnlyLifted = true;
            await attempt.Work.ReportStepAsync(
                OpenStep + "-readonly",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["readonly"] = "lifted" },
                [TargetArtifact.Created(ReadOnlySlot, ArtifactRoles.Lock, dataset.SdPath, locator: "readonly")],
                ct).ConfigureAwait(false);
        }

        // The lock is asked for under the delivery's own id, named before the request so an answer lost after the service took
        // it still leaves its release to the undo.
        await attempt.Work.ReportStepAsync(
            OpenStep + "-intent",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["lockId"] = attempt.LockId ?? throw new InvalidOperationException("A dataset is opened under a lock id.") },
            [TargetArtifact.Intent(LockSlot, ArtifactRoles.Lock, attempt.LockId, dataset.SdPath)],
            ct).ConfigureAwait(false);
        var url = OsduHttpClient.WithQuery(DatasetUrl(attempt.Route, dataset, "/lock"), "openmode", "write");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LockHeader] = attempt.LockId ?? throw new InvalidOperationException("A dataset is opened under a lock id."),
        };
        var result = await _client.SendJsonAsync(HttpMethod.Put, url, null, new HashSet<int> { 400, 404, 423 }, ct, idempotent: true, headers: headers).ConfigureAwait(false);
        switch ((int)result.Status)
        {
            case 404:
                return null;
            case 400:
                throw new RecordHeldException(
                    $"Seismic Store refuses to open {dataset.SdPath} for writing: {HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}. A read-only dataset opens once its flag is lifted (readOnly: true on the DDMS lifts it itself)");
            case 423:
                throw new DeliveryException(
                    $"Seismic Store keeps {dataset.SdPath} locked for another reader or writer ({HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}); the next try opens it again.");
        }

        // The lock is this try's from here, whatever the dataset says next, and the unit's until the close releases it.
        attempt.HoldsLock = true;
        await attempt.Work.ReportStepAsync(
            OpenStep,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["lockId"] = attempt.LockId },
            [TargetArtifact.Created(LockSlot, ArtifactRoles.Lock, dataset.SdPath, locator: attempt.LockId)],
            ct).ConfigureAwait(false);
        var answered = OsduHttpClient.ParseJson(result, url);
        CheckNotDeleting(dataset, answered);
        return (Text(answered, "gcsurl"), Header(result, ProviderHeader));
    }

    private async Task CloseAsync(Attempt attempt, JsonObject body, CancellationToken ct)
    {
        var dataset = attempt.Dataset;
        var started = attempt.Steps.Now;
        var lockId = attempt.LockId ?? throw new InvalidOperationException("A dataset is closed under a lock id.");
        var url = OsduHttpClient.WithQuery(DatasetUrl(attempt.Route, dataset, string.Empty), "close", lockId);
        var result = await _client.SendJsonAsync(HttpMethod.Patch, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            // The service released no lock of this delivery's: another writer holds the dataset, or it is gone.
            attempt.HoldsLock = false;
            throw new DeliveryException(
                $"Seismic Store refused to close {dataset.SdPath} under this delivery's lock ({HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}); the lock lapsed or another writer holds the dataset, and the next try opens it again.");
        }

        attempt.HoldsLock = false;
        var answered = OsduHttpClient.ParseJson(result, url);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Text(answered, "ctag") is { } ctag)
        {
            values["seismicStore.ctag"] = ctag;
        }

        attempt.Steps.Add(CloseStep, started, (int)result.Status, values);

        // The close released the lock, and set the read-only flag as the flow closes datasets: neither is left to undo.
        var released = new List<TargetArtifact>
        {
            TargetArtifact.Created(LockSlot, ArtifactRoles.Lock, dataset.SdPath, locator: lockId) with { Status = ArtifactStatus.Removed, Note = "released by the close" },
        };
        if (attempt.ReadOnlyLifted)
        {
            released.Add(TargetArtifact.Created(ReadOnlySlot, ArtifactRoles.Lock, dataset.SdPath, locator: "readonly") with { Status = ArtifactStatus.Removed, Note = "set again by the close" });
        }

        await attempt.Work.ReportStepAsync(CloseStep, values, released, ct).ConfigureAwait(false);
        Merge(attempt.Returned, values);
    }

    private async Task PatchAsync(Attempt attempt, JsonObject body, CancellationToken ct)
    {
        var url = DatasetUrl(attempt.Route, attempt.Dataset, string.Empty);
        var result = await _client.SendJsonAsync(HttpMethod.Patch, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            throw new RecordHeldException(
                $"Seismic Store no longer holds the dataset {attempt.Dataset.SdPath}, whose record this delivery changes; redeliver the record with its files to register the dataset again");
        }
    }

    /// <summary>
    /// Releases the write lock held under <paramref name="lockId"/> alone (<c>PATCH ...?close={id}</c> with an empty body), never
    /// another writer's: a dataset locked under another id, or no longer there, answers 404. Returns whether a lock was released.
    /// </summary>
    private async Task<bool> ReleaseLockAsync(DdmsRoute route, SeismicDataset dataset, string lockId, CancellationToken ct)
    {
        var url = OsduHttpClient.WithQuery(DatasetUrl(route, dataset, string.Empty), "close", lockId);
        var result = await _client.SendJsonAsync(HttpMethod.Patch, url, new JsonObject(), new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        return (int)result.Status != 404;
    }

    /// <summary>Releases the write lock a try that stops here holds, so the dataset is not locked for the lock's day.</summary>
    private async Task ReleaseAsync(Attempt attempt, CancellationToken ct)
    {
        try
        {
            await ReleaseLockAsync(attempt.Route, attempt.Dataset, attempt.LockId ?? throw new InvalidOperationException("A lock is released under its id."), ct).ConfigureAwait(false);
            attempt.HoldsLock = false;
            await attempt.Work.ReportStepAsync(
                "unlock",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["lockId"] = attempt.LockId ?? string.Empty },
                [TargetArtifact.Created(LockSlot, ArtifactRoles.Lock, attempt.Dataset.SdPath, locator: attempt.LockId) with { Status = ArtifactStatus.Removed, Note = "released when the delivery was held" }],
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException)
        {
            // The hold that led here is what the record reports; the lock lapses on its own.
            _logger.LogWarning(
                "The write lock this delivery took on {Dataset} was not released ({Message}); it lapses after a day, and the record's next delivery takes it as its own.",
                attempt.Dataset.SdPath,
                HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    private async Task<JsonElement?> GetDatasetAsync(DdmsRoute route, SeismicDataset dataset, CancellationToken ct)
    {
        var url = OsduHttpClient.WithQuery(DatasetUrl(route, dataset, string.Empty), "translate-user-info", "false");
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        return (int)result.Status == 404 ? null : OsduHttpClient.ParseJson(result, url);
    }

    /// <summary>The provider the service names on its unauthenticated status (<c>GET /svcstatus</c>).</summary>
    private async Task<DdmsProvider> ServiceProviderAsync(DdmsRoute route, CancellationToken ct)
    {
        var result = await _client.SendJsonAsync(HttpMethod.Get, _client.Url(route.Service.Root + "/svcstatus"), null, null, ct).ConfigureAwait(false);
        var label = Header(result, ProviderHeader);
        return ProviderOf(label)
            ?? throw new RecordHeldException(
                $"{DdmsRouting.Describe(route.Service)} names its provider '{label ?? "(none)"}', which tells the route no object store to upload to; declare provider (azure, gc, anthos or ibm) on the DDMS");
    }

    /// <summary>Holds a record whose files cannot go to the store of <paramref name="provider"/> as the flow describes it, before credentials are asked for.</summary>
    private static void CheckProvider(Attempt attempt, DdmsProvider provider)
    {
        if (provider == DdmsProvider.Aws)
        {
            throw new RecordHeldException(
                $"{DdmsRouting.Describe(attempt.Route.Service)} runs on aws, whose upload credentials osdu/specs/seismic-ddms/INTEGRATION.md section 4.3 does not describe; the route writes files on azure, gc, anthos and ibm");
        }

        if (provider is DdmsProvider.Anthos or DdmsProvider.Ibm && attempt.Settings.ObjectStore is null)
        {
            throw new RecordHeldException(
                $"{DdmsRouting.Describe(attempt.Route.Service)} runs on {ProviderLabel(provider)} and issues a key triple for an S3 store it does not name; declare objectStore (the store's endpoint) on the DDMS");
        }
    }

    /// <summary>Upload credentials for the dataset (<c>GET /utility/upload-connection-string?sdpath=</c>).</summary>
    private async Task<SeismicCredential> CredentialAsync(DdmsRoute route, SeismicDataset dataset, CancellationToken ct)
    {
        var url = OsduHttpClient.WithQuery(_client.Url(route.Service.Root + "/utility/upload-connection-string"), "sdpath", dataset.SdPath);
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
        JsonElement answered;
        try
        {
            using var parsed = JsonDocument.Parse(result.Body);
            answered = parsed.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{DdmsRouting.Describe(route.Service)} answered the upload credentials of {dataset.SdPath} with a body that is not JSON.", ex);
        }

        var type = Text(answered, "token_type");
        var token = Text(answered, "access_token");
        return type is null || token is null
            ? throw new DeliveryException($"{DdmsRouting.Describe(route.Service)} answered the upload credentials of {dataset.SdPath} without a token and its type.")
            : new SeismicCredential(type, token);
    }

    private async Task<SeismicObjectStore> StoreAsync(Attempt attempt, DdmsProvider provider, string location, CancellationToken ct)
    {
        var route = attempt.Route;
        var dataset = attempt.Dataset;
        var credential = await CredentialAsync(route, dataset, ct).ConfigureAwait(false);
        Task<SeismicCredential> Renew(CancellationToken token) => CredentialAsync(route, dataset, token);
        if (string.Equals(credential.TokenType, "SasUrl", StringComparison.OrdinalIgnoreCase))
        {
            return provider == DdmsProvider.Azure
                ? new AzureBlobStore(_client, credential, Renew)
                : throw new RecordHeldException($"{DdmsRouting.Describe(route.Service)} issued an Azure SAS for a {ProviderLabel(provider)} deployment; declare the provider the deployment runs on");
        }

        if (!string.Equals(credential.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new RecordHeldException($"{DdmsRouting.Describe(route.Service)} issued upload credentials of the type {credential.TokenType}, which no object store this route writes to takes");
        }

        switch (provider)
        {
            case DdmsProvider.Gc:
                {
                    var (bucket, prefix) = Split(location, "/");
                    var endpoint = attempt.Settings.ObjectStore is { } google ? new Uri(google) : new Uri(GoogleStore.DefaultEndpoint);
                    return new GoogleStore(_client, endpoint, bucket, prefix, credential, Renew);
                }

            case DdmsProvider.Anthos or DdmsProvider.Ibm:
                {
                    var endpoint = attempt.Settings.ObjectStore
                        ?? throw new InvalidOperationException("The object store of an S3 deployment is checked before its credentials are asked for.");
                    var (bucket, prefix) = location.Contains("$$", StringComparison.Ordinal) ? Split(location, "$$") : Split(location, "/");
                    return new S3Store(_client, new Uri(endpoint), bucket, prefix, credential, attempt.Settings.Region, _time, Renew);
                }

            default:
                throw new RecordHeldException($"{DdmsRouting.Describe(route.Service)} issued a bearer credential on {ProviderLabel(provider)}, which issues SAS URLs; declare the provider the deployment runs on");
        }
    }

    /// <summary>
    /// Uploads the dataset's objects in order. The step records how many objects landed, so a later try reads past them
    /// (hashing them, since the file's MD5 covers every byte) and sends the rest; a layout that differs from the one an
    /// earlier try used starts over.
    /// </summary>
    private async Task<Upload> UploadAsync(Attempt attempt, DdmsProvider provider, string location, StoreOnDemand store, CancellationToken ct)
    {
        var work = attempt.Work;
        var plan = attempt.Plan;
        var chunkBytes = (long)attempt.Settings.ChunkMiB * MiB;
        var partBytes = (attempt.Settings.ChunkMiB > 0 ? attempt.Settings.ChunkMiB : SeismicStoreSettings.DefaultChunkMiB) * MiB;
        var layout = Layout(plan, provider, chunkBytes);
        var keys = layout.Select(o => o.Key).ToList();
        var fingerprint = Fingerprint(plan, provider, chunkBytes, location);
        var done = 0;
        if (work.Completed(UploadStep) is { } progress && progress.GetValueOrDefault("layout") == fingerprint)
        {
            if (progress.GetValueOrDefault("complete") == "true")
            {
                attempt.Steps.Resumed(UploadStep, progress);
                return new Upload(progress, keys, 0);
            }

            done = int.TryParse(progress.GetValueOrDefault("objects"), NumberStyles.None, CultureInfo.InvariantCulture, out var landed) ? Math.Clamp(landed, 0, layout.Count) : 0;
        }

        if (provider == DdmsProvider.Azure && layout.FirstOrDefault(o => (o.Length + partBytes - 1) / partBytes > AzureBlobStore.MaxBlocks) is { } oversized)
        {
            var limit = string.Create(CultureInfo.InvariantCulture, $"{oversized.Length} bytes, more than an Azure blob of {AzureBlobStore.MaxBlocks} blocks of {partBytes / MiB} MiB holds");
            throw new RecordHeldException(
                $"the object {oversized.Key} of {plan.Dataset.SdPath} is {limit}; set chunkMiB, the size of each block and of each blob a single file is cut into, higher");
        }

        var payload = work.Payload ?? throw new RecordHeldException("the record needs its files but none are attached");
        var objects = await store.GetAsync(ct).ConfigureAwait(false);
        var started = attempt.Steps.Now;
        var index = 0;
        var sent = 0;
        long bytes = 0;
        string? md5 = null;
        for (var file = 0; file < plan.Files.Count; file++)
        {
            await using var reader = await PartReader.OpenAsync(payload, plan.Files[file], ct).ConfigureAwait(false);
            foreach (var item in layout.Where(o => o.File == file))
            {
                if (index < done)
                {
                    await reader.SkipAsync(item.Length, ct).ConfigureAwait(false);
                }
                else
                {
                    await objects.PutAsync(item.Key, item.Length, reader, partBytes, reader.Md5, ct).ConfigureAwait(false);
                    sent++;
                }

                index++;
                bytes += item.Length;
                if (index > done && index < layout.Count && index % ProgressObjects == 0)
                {
                    await work.ReportStepAsync(UploadStep, Progress(fingerprint, index, bytes, complete: false), ct).ConfigureAwait(false);
                }
            }

            await reader.EndAsync(ct).ConfigureAwait(false);
            if (plan.Files.Count == 1)
            {
                md5 = Convert.ToHexStringLower(reader.Md5());
            }
        }

        var values = Progress(fingerprint, layout.Count, bytes, complete: true);
        values[ObjectsKey] = layout.Count.ToString(CultureInfo.InvariantCulture);
        values[SizeKey] = bytes.ToString(CultureInfo.InvariantCulture);
        values[LayoutKey] = plan.Files.Count == 1 ? ChunkLayout : FileLayout;
        values["seismicStore.store"] = objects.Describe;
        if (md5 is not null)
        {
            values[Md5Key] = md5;
        }

        if (plan.Files.Count > 1)
        {
            var names = string.Join('/', plan.Names);
            if (names.Length <= MaxNamesLength)
            {
                values[NamesKey] = names;
            }
        }

        attempt.Steps.Add(UploadStep, started, null, values);
        await work.ReportStepAsync(UploadStep, values, ct).ConfigureAwait(false);
        _logger.LogInformation("Uploaded {Objects} object(s) of {Dataset} to {Store} ({Sent} by this try).", layout.Count, plan.Dataset.SdPath, objects.Describe, sent);
        return new Upload(values, keys, sent);
    }

    /// <summary>
    /// The record's version in Storage, which Seismic Store does not return. A record Seismic Store did not write (a
    /// deployment with its Storage writes turned off) is written through Storage: one Storage does not hold, and one this
    /// delivery sent that is still at the version the ledger holds, since every write Seismic Store makes is a new version.
    /// </summary>
    private async Task<long?> RecordVersionAsync(Attempt attempt, CancellationToken ct)
    {
        var work = attempt.Work;
        var read = await RecordWriter.VerifyAsync(_client, attempt.Route.RecordPath, work.TargetId, null, ct).ConfigureAwait(false);
        if (read.Outcome == VerifyOutcome.Missing)
        {
            if (!work.DeliverMetadata && !attempt.RecordWritten)
            {
                throw new RecordHeldException($"Storage does not hold {work.TargetId}, whose dataset this delivery wrote; redeliver the record to write it again");
            }

            return await WriteThroughStorageAsync(attempt, "Seismic Store did not write the record to Storage, so the route wrote it", ct).ConfigureAwait(false);
        }

        if (work.DeliverMetadata && work.ExistingVersion is { } before && read.ObservedVersion == before)
        {
            return await WriteThroughStorageAsync(attempt, "Seismic Store left the record at the version the ledger holds, so the route wrote it to Storage", ct).ConfigureAwait(false);
        }

        return read.ObservedVersion;
    }

    private async Task<long?> WriteThroughStorageAsync(Attempt attempt, string detail, CancellationToken ct)
    {
        var (version, _) = await RecordWriter.SendAsync(_client, _options with { VersionPath = "recordIdVersions[0]" }, attempt.Route.RecordsPath, "PUT", attempt.Document, ct).ConfigureAwait(false);
        attempt.Details.Add(detail);
        if (!attempt.RecordWritten)
        {
            await RecordWrittenAsync(attempt, ct).ConfigureAwait(false);
        }

        return version;
    }

    /// <summary>
    /// Names the record before a call that carries it (<c>seismicmeta</c>) goes: Seismic Store writes it through storage, and a
    /// call whose answer is lost after it landed leaves the record, or its new version, for the undo.
    /// </summary>
    private static Task RecordIntentAsync(Attempt attempt, CancellationToken ct)
        => attempt.Work.ReportStepAsync(
            OsduDdmsProtocol.MetadataStep + "-intent",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = attempt.Work.TargetId },
            [TargetArtifact.RecordWritten(attempt.Work.TargetId, null, attempt.Work.ExistingVersion) with { Status = ArtifactStatus.Intent }],
            ct);

    private static async Task RecordWrittenAsync(Attempt attempt, CancellationToken ct)
    {
        attempt.RecordWritten = true;
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = attempt.Work.TargetId };
        var now = attempt.Steps.Now;
        attempt.Steps.Add(OsduDdmsProtocol.MetadataStep, now, null, values);

        // The record goes with the dataset's registration or close, before the files land or its version is read: it is the
        // unit's until the delivery completes. Seismic Store does not say which version it wrote.
        await attempt.Work.ReportStepAsync(
            OsduDdmsProtocol.MetadataStep, values, [TargetArtifact.RecordWritten(attempt.Work.TargetId, null, attempt.Work.ExistingVersion)], ct).ConfigureAwait(false);
    }

    private SeismicDataset DatasetOf(DdmsRoute route, string targetId)
    {
        var settings = Settings(route);
        var tenant = settings.Tenant ?? _client.Header(FlowMapper.PartitionHeader)
            ?? throw new RecordHeldException($"{DdmsRouting.Describe(route.Service)} names no tenant, and the flow sends no {FlowMapper.PartitionHeader} to take it from");
        if (!DdmsCatalog.IsSeismicTenant(tenant))
        {
            throw new RecordHeldException($"the tenant '{tenant}' is not a Seismic Store tenant name (letters, digits, '_', '.' and '-')");
        }

        var name = DdmsShapeValues.EntityId(targetId)
            ?? throw new RecordHeldException($"the record id '{targetId}' names no key after its type, which names the record's dataset");
        return DdmsCatalog.IsSeismicDataset(name)
            ? new SeismicDataset(tenant, settings.Subproject, settings.Folder, name)
            : throw new RecordHeldException(
                $"the key '{name}' of {targetId} cannot name a Seismic Store dataset, whose name takes letters, digits, '_', '.' and '-' (osdu/specs/seismic-ddms/INTEGRATION.md section 2.1)");
    }

    private Uri DatasetUrl(DdmsRoute route, SeismicDataset dataset, string suffix)
    {
        var url = _client.Url(
            route.Service.Root + "/dataset/tenant/{tenant}/subproject/{subproject}/dataset/{name}" + suffix,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tenant"] = dataset.Tenant,
                ["subproject"] = dataset.Subproject,
                ["name"] = dataset.Name,
            });
        return dataset.Folder is null ? url : OsduHttpClient.WithQuery(url, "path", dataset.Folder);
    }

    /// <summary>A dataset whose delete Seismic Store started and has not finished is not written; the next try finds it gone.</summary>
    private static void CheckNotDeleting(SeismicDataset dataset, JsonElement found)
    {
        if (Text(found, "status") is { } status && status.StartsWith(DeletingStatus, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"Seismic Store is deleting the dataset {dataset.SdPath} (status {status}); the next try registers it again once the delete has finished. "
                + "A delete that stopped part-way is finished by removing the record at the everything scope.");
        }
    }

    private static SeismicStoreSettings Settings(DdmsRoute route)
        => route.Service.SeismicStore ?? throw new InvalidOperationException($"{DdmsRouting.Describe(route.Service)} has no Seismic Store settings.");

    private static DdmsRoute RouteOf(DdmsRecordPaths paths)
        => paths.Route ?? throw new InvalidOperationException($"{paths.EntityType} records have no Seismic Store to go to.");

    private static IReadOnlyDictionary<string, string>? Resume(DeliveryWork work, string step, DeliverySteps steps)
    {
        if (work.Completed(step) is not { } values)
        {
            return null;
        }

        steps.Resumed(step, values);
        return values;
    }

    private static string? Took(IReadOnlyDictionary<string, string>? registration) => registration?.GetValueOrDefault(TookValue);

    private static string? Value(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static bool IsLockId(string id) => id.Length > 1 && id[0] == 'W' && id.All(char.IsAsciiLetterOrDigit);

    private static long? KnownSize(IReadOnlyDictionary<string, string> state)
        => long.TryParse(state.GetValueOrDefault(SizeKey), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null;

    /// <summary>The objects a dataset's file metadata counts, or null when it counts none a reader could use.</summary>
    private static int? Objects(JsonElement dataset)
        => dataset.ValueKind == JsonValueKind.Object
           && dataset.TryGetProperty("filemetadata", out var metadata)
           && metadata.ValueKind == JsonValueKind.Object
           && metadata.TryGetProperty("nobjects", out var count)
           && count.ValueKind == JsonValueKind.Number
           && count.TryGetInt32(out var objects)
           && objects >= 0
            ? objects
            : null;

    private static void Merge(Dictionary<string, string> into, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (name, value) in values)
        {
            if (name.StartsWith("seismicStore.", StringComparison.Ordinal))
            {
                into[name] = value;
            }
        }
    }

    private static JsonObject FileMetadata(Upload upload)
    {
        var metadata = new JsonObject
        {
            ["type"] = "GENERIC",
            ["size"] = long.Parse(upload.Values[SizeKey], CultureInfo.InvariantCulture),
            ["nobjects"] = int.Parse(upload.Values[ObjectsKey], CultureInfo.InvariantCulture),
        };
        if (upload.Values.TryGetValue(Md5Key, out var md5))
        {
            metadata["md5Checksum"] = md5;
        }

        return metadata;
    }

    private static Dictionary<string, string> Progress(string fingerprint, int objects, long bytes, bool complete) => new(StringComparer.Ordinal)
    {
        ["layout"] = fingerprint,
        ["objects"] = objects.ToString(CultureInfo.InvariantCulture),
        ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
        ["complete"] = complete ? "true" : "false",
    };

    /// <summary>What an upload's progress is measured against: the store, the location, the chunk size and every file's name and size.</summary>
    private static string Fingerprint(SeismicPlan plan, DdmsProvider provider, long chunkBytes, string location)
        => Hashing.ContentHash.OfParts(
            [ProviderLabel(provider), location, chunkBytes.ToString(CultureInfo.InvariantCulture), .. plan.Files.Select((f, i) => plan.Names[i] + ":" + f.Size.ToString(CultureInfo.InvariantCulture))]);

    private static string ProviderLabel(DdmsProvider provider) => provider.ToString().ToLowerInvariant();

    private static (string Bucket, string Prefix) Split(string location, string separator)
    {
        var at = location.IndexOf(separator, StringComparison.Ordinal);
        var bucket = at < 0 ? location : location[..at];
        var prefix = at < 0 ? string.Empty : location[(at + separator.Length)..];
        return bucket.Length == 0
            ? throw new DeliveryException($"Seismic Store keeps a dataset's files at '{location}', which names no bucket.")
            : (bucket, prefix);
    }

    private static string FirstLegalTag(JsonObject document)
        => (document["legal"]?["legaltags"] as JsonArray)?.Select(Text).FirstOrDefault(t => t is not null)
            ?? throw new RecordHeldException("the record names no legal tag, which its dataset takes as its ltag");

    private static string? Header(HttpFetchResult result, string name)
        => result.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool NonEmpty(JsonNode? node) => node is JsonArray array && array.Any(item => Text(item) is not null);

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
