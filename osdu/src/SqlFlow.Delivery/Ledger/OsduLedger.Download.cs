using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The downloads of download flows (osdu/docs/reference/flow/download.md, What a run records): every record an OSDU kind holds
// in a partition, kept under the download flow's ledger with the file it is written to and the version that file holds.
public sealed partial class OsduLedger
{
    public async Task<DownloadState> RegisterDownloadAsync(Guid flowId, string flowName, string name, string kind, string? query, string readMode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(readMode);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        await using var db = Open();
        var row = await db.DeliveryDownloads.FirstOrDefaultAsync(d => d.PartitionId == partition && d.FlowId == flowId && d.Name == name, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new DeliveryDownload { PartitionId = partition, FlowId = flowId, Name = name, CreatedUtc = now };
            db.DeliveryDownloads.Add(row);
        }

        row.FlowName = flowName.Length <= DeliveryLedger.MaxFlowNameLength ? flowName : flowName[..DeliveryLedger.MaxFlowNameLength];
        row.Kind = kind;
        row.Query = query;
        row.ReadMode = readMode;
        row.UpdatedUtc = now;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (row.DownloadId == 0)
        {
            // Another process registered it between the read and the write: the row it wrote is the download.
            await using var again = Open();
            row = await again.DeliveryDownloads.AsNoTracking().FirstAsync(d => d.PartitionId == partition && d.FlowId == flowId && d.Name == name, ct).ConfigureAwait(false);
        }

        return await ToDownloadStateAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<long> StartDownloadRunAsync(
        Guid flowId, int downloadId, Guid? runId, string actor, string readMode, string location, string? rewrite, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(readMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (location.Length > DeliveryModel.MaxDownloadLocationLength)
        {
            throw new DeliveryException($"The download's folder '{location[..200]}...' is {location.Length} characters; a download keeps at most {DeliveryModel.MaxDownloadLocationLength}.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await db.DeliveryDownloadRuns
            .Where(r => r.PartitionId == partition && r.DownloadId == downloadId && r.Status == DownloadRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, DownloadRunStatus.Failed)
                .SetProperty(r => r.CompletedUtc, nowUtc)
                .SetProperty(r => r.Error, "the run ended without finishing: its process stopped before it did"), ct).ConfigureAwait(false);
        var earlier = db.DeliveryDownloadRuns.Where(r => r.PartitionId == partition && r.DownloadId == downloadId).Select(r => r.DownloadRunId);
        await db.DeliveryDownloadScans.Where(s => s.PartitionId == partition && earlier.Contains(s.DownloadRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var run = new DeliveryDownloadRun
        {
            PartitionId = partition,
            DownloadId = downloadId,
            RunId = runId,
            Actor = actor.Length <= 200 ? actor : actor[..200],
            Status = DownloadRunStatus.Running,
            StartedUtc = nowUtc,
            ReadMode = readMode,
            Location = location,
            Rewrite = rewrite,
        };
        db.DeliveryDownloadRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return run.DownloadRunId;
    }

    public async Task AppendDownloadScanAsync(Guid flowId, long downloadRunId, IReadOnlyList<DownloadScanRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.AppendDownloadScanAsync(db, partition, downloadRunId, rows, ct), ct).ConfigureAwait(false);
    }

    public async Task<DownloadMerge> MergeDownloadAsync(Guid flowId, int downloadId, long downloadRunId, DateTime nowUtc, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.MergeDownloadAsync(db, partition, downloadId, downloadRunId, nowUtc, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadDue>> DownloadsDueAsync(Guid flowId, int downloadId, bool every, long after, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        return await ReadAsync(
            db => db.DeliveryDownloadRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.DownloadId == downloadId && r.DownloadRecordId > after && r.GoneUtc == null
                    && (every || r.WrittenVersion == null || (r.Version != null && r.WrittenVersion < r.Version)))
                .OrderBy(r => r.DownloadRecordId)
                .Take(max)
                .Select(r => new DownloadDue(r.DownloadRecordId, r.TargetId, r.Version, r.FileName))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task RecordDownloadWritesAsync(Guid flowId, long downloadRunId, IReadOnlyList<DownloadWrite> writes, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.RecordDownloadWritesAsync(db, partition, downloadRunId, writes, nowUtc, ct), ct).ConfigureAwait(false);
    }

    public async Task CompleteDownloadRunAsync(Guid flowId, long downloadRunId, DownloadRunState outcome, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var run = await db.DeliveryDownloadRuns.FirstOrDefaultAsync(r => r.PartitionId == partition && r.DownloadRunId == downloadRunId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Download run {downloadRunId} is not in the ledger of {flowId:D}.");
        run.Status = outcome.Status;
        run.CompletedUtc = nowUtc;
        run.Listed = outcome.Listed;
        run.Pages = outcome.Pages;
        run.Requests = outcome.Requests;
        run.Added = outcome.Added;
        run.Changed = outcome.Changed;
        run.Gone = outcome.Gone;
        run.Returned = outcome.Returned;
        run.Written = outcome.Written;
        run.Bytes = outcome.Bytes;
        run.Missing = outcome.Missing;
        run.Error = RunError(outcome.Error);
        if (outcome.Status == DownloadRunStatus.Completed)
        {
            var download = await db.DeliveryDownloads.FirstAsync(d => d.PartitionId == partition && d.DownloadId == run.DownloadId, ct).ConfigureAwait(false);
            download.LastRunId = run.DownloadRunId;
            download.LastCompletedUtc = nowUtc;
            download.Location = run.Location;
            download.UpdatedUtc = nowUtc;
        }
        else
        {
            // A failed run's staged listing is never merged; the merge of a completed one consumed it already.
            await db.DeliveryDownloadScans.Where(s => s.PartitionId == partition && s.DownloadRunId == downloadRunId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadState>> ListDownloadsAsync(Guid flowId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryDownloads.AsNoTracking()
                .Where(d => d.PartitionId == partition && d.FlowId == flowId)
                .OrderBy(d => d.Name)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var states = new List<DownloadState>(rows.Count);
        foreach (var row in rows)
        {
            states.Add(await ToDownloadStateAsync(row, ct).ConfigureAwait(false));
        }

        return states;
    }

    public async Task<DownloadState?> GetDownloadAsync(string partition, int downloadId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        var row = await ReadAsync(db => db.DeliveryDownloads.AsNoTracking().FirstOrDefaultAsync(d => d.PartitionId == id && d.DownloadId == downloadId, ct), ct).ConfigureAwait(false);
        return row is null ? null : await ToDownloadStateAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<DownloadCounts> DownloadCountsAsync(string partition, int downloadId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return new DownloadCounts(0, 0, 0, 0, 0);
        }

        var counts = await ReadAsync(
            db => db.DeliveryDownloadRecords.AsNoTracking()
                .Where(r => r.PartitionId == id && r.DownloadId == downloadId)
                .GroupBy(r => 1)
                .Select(g => new
                {
                    Gone = g.LongCount(r => r.GoneUtc != null),
                    Problem = g.LongCount(r => r.GoneUtc == null && r.Problem != null),
                    Due = g.LongCount(r => r.GoneUtc == null && r.Problem == null && (r.WrittenVersion == null || (r.Version != null && r.WrittenVersion < r.Version))),
                    All = g.LongCount(),
                    Bytes = g.Sum(r => r.Bytes ?? 0),
                })
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
        return counts is null
            ? new DownloadCounts(0, 0, 0, 0, 0)
            : new DownloadCounts(counts.All - counts.Gone - counts.Problem - counts.Due, counts.Due, counts.Problem, counts.Gone, counts.Bytes);
    }

    public async Task<IReadOnlyList<DownloadRunState>> ListDownloadRunsAsync(string partition, int downloadId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        return await ReadAsync(
            db => db.DeliveryDownloadRuns.AsNoTracking()
                .Where(r => r.PartitionId == id && r.DownloadId == downloadId)
                .OrderByDescending(r => r.DownloadRunId)
                .Take(limit)
                .Select(r => ToDownloadRunState(r))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadRecordState>> ListDownloadRecordsAsync(
        string partition, int downloadId, string? state, string? search, long? after, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (state is not null && !DownloadRecordStates.IsKnown(state))
        {
            throw new DeliveryException($"'{state}' is not where a downloaded record stands; it is one of {string.Join(", ", DownloadRecordStates.All)}.");
        }

        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var from = after ?? 0;
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        return await ReadAsync(
            db =>
            {
                var rows = db.DeliveryDownloadRecords.AsNoTracking().Where(r => r.PartitionId == id && r.DownloadId == downloadId && r.DownloadRecordId > from);
                rows = state switch
                {
                    DownloadRecordStates.Gone => rows.Where(r => r.GoneUtc != null),
                    DownloadRecordStates.Problem => rows.Where(r => r.GoneUtc == null && r.Problem != null),
                    DownloadRecordStates.Due => rows.Where(r => r.GoneUtc == null && r.Problem == null
                        && (r.WrittenVersion == null || (r.Version != null && r.WrittenVersion < r.Version))),
                    DownloadRecordStates.Written => rows.Where(r => r.GoneUtc == null && r.Problem == null && r.WrittenVersion != null
                        && (r.Version == null || r.WrittenVersion >= r.Version)),
                    _ => rows,
                };
                if (text is not null)
                {
                    // The id is kept exactly; a person finds it by the spelling they know, whatever its case, as the file name is compared.
                    rows = rows.Where(r => EF.Functions.Collate(r.TargetId, DeliveryModel.SearchCollation).Contains(text) || r.FileName.Contains(text));
                }

                return rows.OrderBy(r => r.DownloadRecordId).Take(limit).Select(r => ToDownloadRecordState(r)).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    private async Task<DownloadState> ToDownloadStateAsync(DeliveryDownload row, CancellationToken ct) => new()
    {
        DownloadId = row.DownloadId,
        Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) ?? string.Empty,
        FlowId = row.FlowId,
        FlowName = row.FlowName,
        Name = row.Name,
        Kind = row.Kind,
        Query = row.Query,
        ReadMode = row.ReadMode,
        Location = row.Location,
        CreatedUtc = DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc),
        UpdatedUtc = DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc),
        LastRunId = row.LastRunId,
        LastCompletedUtc = row.LastCompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
    };

    private static DownloadRunState ToDownloadRunState(DeliveryDownloadRun r) => new()
    {
        DownloadRunId = r.DownloadRunId,
        DownloadId = r.DownloadId,
        RunId = r.RunId,
        Actor = r.Actor,
        Status = r.Status,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        ReadMode = r.ReadMode,
        Location = r.Location,
        Rewrite = r.Rewrite,
        Listed = r.Listed,
        Pages = r.Pages,
        Requests = r.Requests,
        Added = r.Added,
        Changed = r.Changed,
        Gone = r.Gone,
        Returned = r.Returned,
        Written = r.Written,
        Bytes = r.Bytes,
        Missing = r.Missing,
        Error = r.Error,
    };

    private static DownloadRecordState ToDownloadRecordState(DeliveryDownloadRecord r) => new()
    {
        DownloadRecordId = r.DownloadRecordId,
        DownloadId = r.DownloadId,
        TargetId = r.TargetId,
        Kind = r.Kind,
        Version = r.Version,
        FileName = r.FileName,
        FirstSeenUtc = DateTime.SpecifyKind(r.FirstSeenUtc, DateTimeKind.Utc),
        ChangedUtc = DateTime.SpecifyKind(r.ChangedUtc, DateTimeKind.Utc),
        GoneUtc = r.GoneUtc is { } gone ? DateTime.SpecifyKind(gone, DateTimeKind.Utc) : null,
        WrittenVersion = r.WrittenVersion,
        WrittenUtc = r.WrittenUtc is { } written ? DateTime.SpecifyKind(written, DateTimeKind.Utc) : null,
        WrittenRunId = r.WrittenRunId,
        Bytes = r.Bytes,
        Sha256 = r.Sha256 is { } hash ? Convert.ToHexStringLower(hash) : null,
        Problem = r.Problem,
        ProblemUtc = r.ProblemUtc is { } problem ? DateTime.SpecifyKind(problem, DateTimeKind.Utc) : null,
    };
}
