using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Reversals;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// How a route reaches the record itself when it undoes a unit (osdu/docs/reference/concepts/artifacts-and-undo.md,
/// The record itself): the route's own reversible removal, the storage read that says when OSDU created the record,
/// and the write-back of an earlier version, each null where the route cannot do it, with why.
/// </summary>
internal sealed record RecordSide
{
    /// <summary>The route's reversible removal of the record (a DDMS's logical delete, storage's soft delete); null when it has none, with <see cref="RemoveRefusal"/>.</summary>
    public Func<string, CancellationToken, Task<DeleteOutcome>>? Remove { get; init; }

    /// <summary>Why the route cannot remove the record reversibly, when <see cref="Remove"/> is null.</summary>
    public string? RemoveRefusal { get; init; }

    /// <summary>The storage read of the record, for when OSDU created it; null when the route does not reach storage.</summary>
    public Func<string, CancellationToken, Task<JsonObject?>>? Read { get; init; }

    /// <summary>The route that writes an earlier version back (<see cref="VersionWriteBack"/>); null when it cannot, with <see cref="RestoreRefusal"/>.</summary>
    public IDeliveryProtocol? Restorer { get; init; }

    /// <summary>Why the route cannot write an earlier version back, when <see cref="Restorer"/> is null.</summary>
    public string? RestoreRefusal { get; init; }

    /// <summary>The versions the target keeps of the record, newest first; null when it keeps no list the route can read.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<long>?>>? Versions { get; init; }
}

/// <summary>One record's file registrations an undo removes: the record's undo work, and the dataset artifacts of it to undo.</summary>
internal sealed record DatasetUndo(UndoWork Work, IReadOnlyList<UndoItem> Items);

/// <summary>
/// The undo steps the routes share (osdu/docs/reference/concepts/artifacts-and-undo.md, What every route's undo
/// shares): soft deletes of the OSDU ids a unit minted through storage, finding a file registration whose answer was
/// lost by where its file landed, and the record itself, removed when the unit created it and given back the version it
/// replaced when the unit updated it. Every step answers each artifact it was given, and a failure is an answer (<see
/// cref="ArtifactStatus.Failed"/>), never an exception, so one artifact that cannot be reached never hides another's
/// undo.
/// </summary>
internal static class ArtifactUndo
{
    /// <summary>
    /// Removes <paramref name="targets"/>, ids one record's unit minted, reversibly through the storage service
    /// (<see cref="SoftDeleteManyAsync"/>).
    /// </summary>
    public static Task<IReadOnlyList<UndoResult>> SoftDeleteAsync(OsduRecordProtocol storage, Identity.DeliveryKey key, IReadOnlyList<(UndoItem Item, string Id)> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return SoftDeleteManyAsync(storage, targets.Select(t => (t.Item, key, t.Id)).ToList(), ct);
    }

    /// <summary>
    /// Removes <paramref name="targets"/>, ids the units of any number of records minted, reversibly through the storage service
    /// in as few calls as storage takes them (<c>POST /records/delete</c>, 500 ids a request, falling back one at a time as a
    /// removal does): each answered removed, gone when storage no longer held it, or failed with what storage said, in the order
    /// given. An id named twice is sent once, and both are answered with what became of it. A failure of the call as a whole
    /// (storage unreachable, throttled past its retries) answers every target with it, so the sweep tries each again.
    /// </summary>
    public static async Task<IReadOnlyList<UndoResult>> SoftDeleteManyAsync(
        OsduRecordProtocol storage, IReadOnlyList<(UndoItem Item, Identity.DeliveryKey Key, string Id)> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            return [];
        }

        var removals = targets
            .DistinctBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new RecordRemoval(t.Key, t.Id, null))
            .ToList();
        IReadOnlyList<RemovalResult> removed;
        try
        {
            removed = await storage.DeleteBatchAsync(removals, RemovalScope.Record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Answerable(ex, ct))
        {
            return targets.Select(t => UndoResult.Failed(t.Item, Redact(ex))).ToList();
        }

        // Storage answers each removal in the order it was given, which is the order of the distinct ids.
        var byId = new Dictionary<string, RemovalResult>(StringComparer.Ordinal);
        for (var i = 0; i < removals.Count && i < removed.Count; i++)
        {
            byId[removals[i].TargetId] = removed[i];
        }

        return targets
            .Select(t => byId.GetValueOrDefault(t.Id) switch
            {
                { Failure: { } failure } => UndoResult.Failed(t.Item, Redact(failure)),
                { Outcome.AlreadyGone: true } => UndoResult.Gone(t.Item, $"{t.Id}: storage no longer holds it"),
                { Outcome: { } outcome } => UndoResult.Removed(t.Item, $"{t.Id}: {outcome.Detail}"),
                _ => UndoResult.Failed(t.Item, "storage gave no answer for it"),
            })
            .ToList();
    }

    /// <summary>
    /// Undoes one record's file registrations (<see cref="DatasetsAsync(OsduHttpClient, ProtocolOptions, OsduRecordProtocol, IReadOnlyList{DatasetUndo}, CancellationToken)"/>).
    /// </summary>
    public static Task<IReadOnlyList<UndoResult>> DatasetsAsync(
        OsduHttpClient client, ProtocolOptions options, OsduRecordProtocol storage, UndoWork work, IReadOnlyList<UndoItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(items);
        return DatasetsAsync(client, options, storage, [new DatasetUndo(work, items)], ct);
    }

    /// <summary>
    /// Undoes file registrations (<see cref="ArtifactRoles.Dataset"/>) of any number of records: every dataset registered for
    /// the landing-zone path a registration names is removed reversibly through storage, found by the index (the path is one
    /// upload's alone, so each is the unit's: a registration whose answer was lost, and the one a later try sent again under the
    /// same slot when the index had not listed the first), with the id the registration answered. One that names no path is
    /// removed by its id. An intent the index lists nothing for is gone: the registration never landed, or the index has not
    /// caught up, which the inventory finds if it did. The index is asked once a path; every dataset found, and every one known
    /// by its id, of every record given, is then removed in as few storage calls as storage takes
    /// (<see cref="SoftDeleteManyAsync"/>), so undoing the datasets of a page of records costs a call per 500 of them rather
    /// than one a record.
    /// </summary>
    public static async Task<IReadOnlyList<UndoResult>> DatasetsAsync(
        OsduHttpClient client, ProtocolOptions options, OsduRecordProtocol storage, IReadOnlyList<DatasetUndo> asked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(asked);
        var results = new List<UndoResult>();
        var targets = new List<(UndoItem Item, Identity.DeliveryKey Key, string Id)>();

        // Which targets answer an item by themselves (known by their id), and which together answer one path's registration.
        var direct = new List<int>();
        var found = new List<(UndoItem Item, int First, IReadOnlyList<string> Ids)>();
        foreach (var (work, items) in asked)
        {
            foreach (var item in items)
            {
                var known = item.Artifact.TargetId;
                if (item.Artifact.Locator is not { } landed)
                {
                    if (known is not null)
                    {
                        direct.Add(targets.Count);
                        targets.Add((item, work.Key, known));
                    }
                    else
                    {
                        results.Add(UndoResult.Kept(item, "the registration names neither the dataset it made nor where its file landed, so nothing can find it"));
                    }

                    continue;
                }

                IReadOnlyList<string> listed;
                try
                {
                    listed = await FileUploads.RegisteredAtAsync(client, options, landed, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (Answerable(ex, ct))
                {
                    results.Add(UndoResult.Failed(item, $"the index could not be asked which dataset {landed} became: {Redact(ex)}"));
                    continue;
                }

                var ids = (known is null ? listed : listed.Append(known)).Distinct(StringComparer.Ordinal).ToList();
                if (ids.Count == 0)
                {
                    results.Add(UndoResult.Gone(item, $"the index lists no dataset registered for {landed}: the registration did not land, or the index has not listed it yet (an inventory of the dataset kind finds it if it did)"));
                    continue;
                }

                // Every dataset registered for the path is this unit's: the landing-zone path is one upload's alone.
                found.Add((item, targets.Count, ids));
                targets.AddRange(ids.Select(id => (item, work.Key, id)));
            }
        }

        var removed = await SoftDeleteManyAsync(storage, targets, ct).ConfigureAwait(false);
        results.AddRange(direct.Select(i => removed[i]));
        results.AddRange(found.Select(f => Combine(f.Item, removed.Skip(f.First).Take(f.Ids.Count).ToList(), f.Ids)));
        return results;
    }

    /// <summary>
    /// Undoes the record itself (<see cref="ArtifactRoles.Record"/> and <see cref="ArtifactRoles.Version"/>): left to newer work
    /// when <see cref="UndoWork.KeepRecord"/>; removed through the route when the unit created it, after storage confirms OSDU
    /// created it after the unit began (a record that existed before is given back the version before the unit's write); given
    /// back the version the unit's write replaced when the unit updated it, where the route can write a version back.
    /// </summary>
    public static async Task<IReadOnlyList<UndoResult>> RecordItselfAsync(UndoWork work, IReadOnlyList<UndoItem> items, RecordSide side, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(side);
        var results = new List<UndoResult>(items.Count);
        foreach (var item in items)
        {
            if (work.KeepRecord)
            {
                results.Add(UndoResult.Superseded(item, "the record's newer work writes it again, so it is left as it is"));
                continue;
            }

            try
            {
                results.Add(item.Artifact.Role == ArtifactRoles.Record
                    ? await CreatedAsync(work, item, side, ct).ConfigureAwait(false)
                    : await UpdatedAsync(work, item, item.Artifact.PriorVersion, side, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, Redact(ex)));
            }
        }

        return results;
    }

    /// <summary>
    /// A record the unit created: storage says when OSDU created it. Created after the unit began (less the clock skew), it is
    /// the unit's, and is removed. Created before, the id was a record another system wrote before this flow claimed it, so it is
    /// given back the newest version older than the one the unit wrote. Without a storage read, it is removed only when the
    /// unit's own step recorded the version it wrote.
    /// </summary>
    private static async Task<UndoResult> CreatedAsync(UndoWork work, UndoItem item, RecordSide side, CancellationToken ct)
    {
        var id = item.Artifact.TargetId ?? work.TargetId;
        if (side.Read is { } read)
        {
            var stored = await read(id, ct).ConfigureAwait(false);
            if (stored is null)
            {
                return UndoResult.Gone(item, $"{id}: OSDU no longer holds the record");
            }

            if (CreateTimeOf(stored) is { } created && created < item.UnitStartedUtc - ArtifactLimits.ClockSkew)
            {
                var before = await VersionBeforeAsync(id, item.Artifact.Version, side, ct).ConfigureAwait(false);
                return before is { } version
                    ? await UpdatedAsync(work, item, version, side, ct).ConfigureAwait(false)
                    : UndoResult.Kept(item, string.Create(CultureInfo.InvariantCulture, $"{id}: OSDU created the record at {created:u}, before this delivery began at {item.UnitStartedUtc:u}, so it is not this delivery's to remove, and which version it held before is not known; nothing was put back"));
            }
        }
        else if (item.Artifact.Version is null)
        {
            return UndoResult.Kept(item, $"{id}: the route does not read storage, and the delivery's step recorded no version it wrote, so whether the delivery created the record cannot be told; it is left as it is");
        }

        if (side.Remove is not { } remove)
        {
            return UndoResult.Kept(item, $"{id}: {side.RemoveRefusal ?? "the route has no reversible removal"}; OSDU keeps the record the unfinished delivery created");
        }

        var outcome = await remove(id, ct).ConfigureAwait(false);
        return outcome.AlreadyGone
            ? UndoResult.Gone(item, $"{id}: {outcome.Detail}")
            : UndoResult.Removed(item, $"{id}: {outcome.Detail}");
    }

    /// <summary>A record the unit wrote a new version of: the version before it, <paramref name="prior"/>, is written back as the next version.</summary>
    private static async Task<UndoResult> UpdatedAsync(UndoWork work, UndoItem item, long? prior, RecordSide side, CancellationToken ct)
    {
        var id = item.Artifact.TargetId ?? work.TargetId;
        if (prior is not { } version)
        {
            return UndoResult.Kept(item, $"{id}: the version the delivery replaced is not known, so nothing was put back");
        }

        // An intent whose answer never came may not have landed: when the latest version is still the one it would have
        // replaced, there is nothing to put back, and writing it again would only add a version.
        if (item.Artifact.Version is null && side.Versions is { } list
            && await list(id, ct).ConfigureAwait(false) is { Count: > 0 } versions && versions.Max() == version)
        {
            return UndoResult.Gone(item, string.Create(CultureInfo.InvariantCulture, $"{id}: OSDU's latest version is still {version}, the one the delivery's write would have replaced, so the write did not land"));
        }

        if (side.Restorer is not { } restorer)
        {
            return UndoResult.Kept(item, $"{id}: {side.RestoreRefusal ?? "the route cannot write an earlier version back"}; OSDU keeps the version the unfinished delivery wrote");
        }

        var written = await VersionWriteBack.RunAsync(restorer, [new WriteBack(work.Key, id, version, item.Artifact.Version, work.TargetState)], 1, ct).ConfigureAwait(false);
        var result = written[0];
        return result.Outcome switch
        {
            WriteBackOutcome.Restored => UndoResult.Restored(item, string.Create(CultureInfo.InvariantCulture, $"{id}: version {version} written back as version {result.NewVersion}"))
                with { Rewrite = result.NewVersion is { } newVersion ? (version, newVersion) : null },
            WriteBackOutcome.VersionMissing => UndoResult.Kept(item, string.Create(CultureInfo.InvariantCulture, $"{id}: OSDU no longer holds version {version} (its earlier versions were purged), so nothing was put back")),
            _ => UndoResult.Failed(item, $"{id}: version {version.ToString(CultureInfo.InvariantCulture)} could not be written back: {result.Failure}"),
        };
    }

    /// <summary>The newest version of the record older than <paramref name="written"/>, the one the unit wrote; null when it cannot be told.</summary>
    private static async Task<long?> VersionBeforeAsync(string id, long? written, RecordSide side, CancellationToken ct)
    {
        if (written is not { } mine || side.Versions is not { } list || await list(id, ct).ConfigureAwait(false) is not { } versions)
        {
            return null;
        }

        return versions.Where(v => v < mine).Select(v => (long?)v).DefaultIfEmpty(null).Max();
    }

    /// <summary>When OSDU created the record, from its <c>createTime</c>; null when it does not say.</summary>
    internal static DateTime? CreateTimeOf(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record["createTime"] is JsonValue value && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
            ? created.UtcDateTime
            : null;
    }

    /// <summary>What finding and removing every dataset registered for one landing path came to, as one answer for its intent.</summary>
    private static UndoResult Combine(UndoItem item, IReadOnlyList<UndoResult> removed, IReadOnlyList<string> found)
    {
        var names = string.Join(", ", found);
        if (removed.Any(r => r.Outcome == ArtifactStatus.Failed))
        {
            return UndoResult.Failed(item, $"registered as {names}: {string.Join("; ", removed.Where(r => r.Outcome == ArtifactStatus.Failed).Select(r => r.Note))}");
        }

        return removed.All(r => r.Outcome == ArtifactStatus.Gone)
            ? UndoResult.Gone(item, $"registered as {names}, which storage no longer holds")
            : UndoResult.Removed(item, $"registered as {names}, removed (reversible)");
    }

    /// <summary>
    /// Why what the record names (its datasets) waits for the record: the record could not be taken back yet, and it still names
    /// them, so they go with it on the next undo; null when nothing of the record failed.
    /// </summary>
    public static string? WaitForRecord(IReadOnlyList<UndoResult> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.FirstOrDefault(r => r.Outcome == ArtifactStatus.Failed) is { } failed
            ? $"the record could not be taken back yet ({failed.Note}), and it still names this; it goes with the record on the next undo"
            : null;
    }

    /// <summary>A failure an undo answers for its artifact rather than throwing: anything but the run being cancelled.</summary>
    internal static bool Answerable(Exception ex, CancellationToken ct)
        => (ex is not OperationCanceledException || !ct.IsCancellationRequested)
           && ex is SqlFlowException or HttpRequestException or IOException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException;

    internal static string Redact(Exception ex) => HeaderRedaction.RedactMessage(ex.Message);

    /// <summary>Every item of <paramref name="items"/> kept, with <paramref name="why"/>.</summary>
    public static IEnumerable<UndoResult> Keep(IEnumerable<UndoItem> items, string why) => items.Select(i => UndoResult.Kept(i, why));
}
