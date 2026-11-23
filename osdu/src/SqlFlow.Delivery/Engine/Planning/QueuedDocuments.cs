using System.Collections.Concurrent;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Planning;

/// <summary>
/// Whether the run a plan is made for can read the documents of the work already queued for the records it plans
/// (osdu/docs/reference/concepts/submissions.md, Work queued where a run cannot read it). A queued record points into a
/// work batch file (<see cref="DocumentRef"/>) at its submission's work location, and a work location on a node's own
/// disk is that node's alone: a part of a split delivery that failed after it planned, a part or a run the platform
/// executes again on another node, and a submission a later run takes over all leave records queued in files the run that
/// plans them next cannot open. Such a record is not passed over as already queued: the plan renders it again, and the
/// render replaces what is queued (<see cref="Delivery.Planning.QueueComparison.UnreadableQueued"/>).
/// </summary>
/// <remarks>
/// The answer is by batch file, asked of the store once for each batch a plan meets and kept for the plan: a batch file is
/// written whole before any record points into it and never written again, so one that is there holds the documents queued
/// in it. A store that does not answer is no answer at all, and the failure is the plan's. One instance serves a plan's
/// every renderer at once.
/// </remarks>
public sealed class QueuedDocuments
{
    private readonly FileStoreRegistry _stores;
    private readonly ILedger _ledger;
    private readonly Guid _submissionId;
    private readonly string _workRoot;
    private readonly ConcurrentDictionary<(Guid SubmissionId, int Batch), Task<bool>> _batches = new();
    private readonly ConcurrentDictionary<Guid, Task<string?>> _roots = new();

    /// <param name="stores">The stores the run reads its work batches through.</param>
    /// <param name="ledger">The ledger, asked where another submission's batches are kept.</param>
    /// <param name="submissionId">The submission the run plans into.</param>
    /// <param name="workRoot">Where the run reads that submission's batches from: its own resolution of the flow's work location.</param>
    public QueuedDocuments(FileStoreRegistry stores, ILedger ledger, Guid submissionId, string workRoot)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        _stores = stores;
        _ledger = ledger;
        _submissionId = submissionId;
        _workRoot = workRoot;
    }

    /// <summary>
    /// Whether the document queued for <paramref name="record"/> is in a batch file the run can read: the batch its
    /// reference names, of the submission that queued it, at the work location the run reads that submission's batches
    /// from. Work that names no submission, no document a batch holds, or a submission that records no work location has no
    /// file to read, and is not readable.
    /// </summary>
    public async ValueTask<bool> ReadableAsync(RecordState record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.LastSubmissionId is not { } submissionId || !DocumentRef.TryParse(record.PendingDocumentRef, out var reference))
        {
            return false;
        }

        if ((submissionId == _submissionId ? _workRoot : await KeptAsync(_roots, submissionId, id => WorkLocationAsync(id, ct)).ConfigureAwait(false)) is not { } root)
        {
            return false;
        }

        return await KeptAsync(_batches, (SubmissionId: submissionId, reference.Batch), key => WorkBatchFile.ExistsAsync(_stores, root, key.SubmissionId, key.Batch, ct))
            .ConfigureAwait(false);
    }

    private async Task<string?> WorkLocationAsync(Guid submissionId, CancellationToken ct)
        => (await _ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false))?.WorkLocation is { Length: > 0 } location ? location : null;

    /// <summary>
    /// The answer kept for <paramref name="key"/>, asked once however many renderers ask at the same moment. An ask that
    /// failed (the store or the ledger did not answer, or the plan was cancelled) is not kept, so the next one asks again.
    /// </summary>
    private static async Task<T> KeptAsync<TKey, T>(ConcurrentDictionary<TKey, Task<T>> kept, TKey key, Func<TKey, Task<T>> ask)
        where TKey : notnull
    {
        var asked = kept.GetOrAdd(key, ask);
        try
        {
            return await asked.ConfigureAwait(false);
        }
        catch
        {
            kept.TryRemove(new KeyValuePair<TKey, Task<T>>(key, asked));
            throw;
        }
    }
}
