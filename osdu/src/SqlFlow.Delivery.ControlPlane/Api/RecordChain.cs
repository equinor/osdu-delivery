using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>What one change of a record's row was, in the ingestion table the record's flow reads.</summary>
public static class SourceChangeKind
{
    /// <summary>The row first reached the table: the moment the ingestion flow inserted it (<c>InsertedDate_DW</c>).</summary>
    public const string Loaded = "loaded";

    /// <summary>The row reached the table again after earlier versions of it: it was deleted and inserted anew.</summary>
    public const string Reloaded = "reloaded";

    /// <summary>
    /// The earliest version of the row the ledger holds, for a record whose arrival the ledger does not know (its table
    /// carries no insert column, or no plan has read the row since the ledger began keeping it): whether it was the row's
    /// insert or a later change cannot be told, so it is named for what it is.
    /// </summary>
    public const string Earliest = "earliest";

    /// <summary>The ingestion flow changed the row: its checksum moved, so the flow stamped it again.</summary>
    public const string Changed = "changed";

    /// <summary>The ingestion flow marked the row deleted: its key match found the row gone from the source.</summary>
    public const string Deleted = "deleted";
}

/// <summary>
/// One platform run a change of a record's row is evidence of: the ingestion run that was writing the record's table when
/// the row was stamped, or the landing run that brought the change's file into the estate. The stage is the estate's own
/// vocabulary (pre-ingestion, ingestion), from the flow's kind.
/// </summary>
public sealed record DeliveryChainRunDto(
    string Stage, Guid RunId, Guid PipelineId, string FlowName, string FlowKind, int Wave, string Status, bool Success,
    DateTime? StartedUtc, DateTime RanUtc, double? DurationSeconds, long Rows, string? Error);

/// <summary>The landing that brought a change's file into the estate: its run, and the file as that run processed it.</summary>
public sealed record DeliveryChainLandingDto(
    DeliveryChainRunDto Run, string FileName, string? FilePath, long Rows, long SizeBytes, DateTimeOffset? FileModifiedUtc);

/// <summary>
/// One change of a record's row in its ingestion table, as the ledger recorded it: what it was (<see cref="SourceChangeKind"/>),
/// the moment the table stamped it, the file and row it came from, the ingestion run that wrote it and the landing that
/// brought its file in. A run is named only when the catalog proves it: <paramref name="Loading"/> was executing at the
/// stamp, and <paramref name="Landing"/> is the last landing of the file before that run began. Either is null when no
/// recorded run qualifies, never filled with a nearby one.
/// </summary>
public sealed record DeliverySourceChangeDto(
    string Kind, DateTime AtUtc, string? FileName, long? RowNumber, DeliveryChainRunDto? Loading, DeliveryChainLandingDto? Landing);

/// <summary>
/// A record's row through its ingestion table: the table, when the row first reached it, and every change of it the
/// ledger recorded, newest first. A run that reloaded the row without changing it leaves no stamp, and so no change: the
/// runs named here are the evidence of a change, never a log of what ran. <paramref name="Truncated"/> says the row
/// changed more often than <see cref="RecordChain.MaxChanges"/> times; the newest are shown, and its arrival always is.
/// </summary>
public sealed record DeliveryRecordChainDto(
    string? SourceTable, DateTime? InsertedUtc, IReadOnlyList<DeliverySourceChangeDto> Changes, bool Truncated, string? Note);

/// <summary>
/// Assembles a record's changes from the ledger and proves each from the catalog. The ingestion flow restamps a row
/// (<c>UpdatedDate_DW</c>) only when its checksum moves, so every distinct stamp the ledger recorded for the record, on
/// the record or on any of its attempts, is one change of the row; a run that reloaded it unchanged left no stamp. The
/// record's arrival is its <c>InsertedDate_DW</c>, and a deletion is the moment the table marked the row deleted.
///
/// Each change is then tied to the runs that made it, from what the platform recorded. The ingestion run is the run of a
/// flow that writes the record's table (what the lineage every flow kind declares says) that was executing when the row
/// was stamped. The landing is the last successful run that processed a file of the change's name before that ingestion
/// run began, which is the landing whose rows it read. Nothing here guesses: with no such run, the change names none.
/// </summary>
internal static class RecordChain
{
    /// <summary>The most changes a chain shows. A row changed more often shows its newest ones, and its arrival.</summary>
    public const int MaxChanges = 50;

    /// <summary>
    /// What a flow kind means in the chain an operator reads. A pre-ingestion flow is one of SQLFlow's file kinds: it
    /// lands whatever a source system dropped, which is where a record enters the estate. Anything else is named by its
    /// own kind rather than forced into a stage it is not.
    /// </summary>
    public static string StageOf(string flowKind) => flowKind switch
    {
        "file" or "pre" or "csv" or "excel" or "xml" or "json" or "parquet" or "sftp" or "copy" => "pre-ingestion",
        "ing" => "ingestion",
        "delivery" => "delivery",
        "retrieval" => "retrieval",
        "cache" => "cache",
        _ => flowKind,
    };

    /// <summary>The changes of one record's row, newest first, each with the runs the catalog proves made it.</summary>
    /// <param name="catalog">The platform's catalog, which holds the runs, the files they processed and the lineage.</param>
    /// <param name="ledger">The ledger, whose attempts recorded the versions of the row.</param>
    /// <param name="record">The record whose changes are wanted.</param>
    /// <param name="sourceTable">
    /// The three-part name of the ingestion table the record's flow reads, as the sync recorded it for the flow's
    /// interface. Without it the ingestion runs cannot be named; the changes and their landings still answer.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<DeliveryRecordChainDto> OfAsync(
        CatalogDbContext catalog, ILedgerQueries ledger, RecordState record, string? sourceTable, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(record);

        var seen = await ledger.ListOriginsAsync(record.FlowId, record.DeliveryKey, MaxChanges * 2, ct).ConfigureAwait(false);
        var (changes, truncated) = Changes(record, seen);
        if (changes.Count == 0)
        {
            return new DeliveryRecordChainDto(
                sourceTable, record.SourceInsertedUtc, [], false,
                "The ledger holds no version of this record's row: no plan has read it with the moment its ingestion table stamped it.");
        }

        var writers = await WritersAsync(catalog, sourceTable, ct).ConfigureAwait(false);
        var found = new List<(Change Change, RunRow? Loading, (RunRow Run, CatalogRunFile File)? Landing)>(changes.Count);
        foreach (var change in changes)
        {
            var loading = await LoadingRunAsync(catalog, writers, change.AtUtc, ct).ConfigureAwait(false);

            // A deletion comes from the ingestion flow's key match, which processes no file: it has no landing.
            var landing = change.Kind == SourceChangeKind.Deleted || change.FileName is null
                ? null
                : await LandingAsync(catalog, change.FileName, loading?.StartedUtc ?? change.AtUtc, ct).ConfigureAwait(false);
            found.Add((change, loading, landing));
        }

        var pipelineIds = found
            .SelectMany(f => new[] { f.Loading?.PipelineId, f.Landing?.Run.PipelineId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var waves = pipelineIds.Count == 0
            ? []
            : await catalog.Pipelines.AsNoTracking()
                .Where(p => pipelineIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Wave })
                .ToDictionaryAsync(p => p.Id, p => p.Wave, ct).ConfigureAwait(false);

        DeliveryChainRunDto Dto(RunRow run) => new(
            StageOf(run.FlowKind), run.RunId, run.PipelineId, run.FlowName, run.FlowKind, waves.GetValueOrDefault(run.PipelineId, -1),
            run.Status, run.Success, run.StartedUtc, run.RanUtc, run.DurationSeconds, run.Rows, run.Error);

        var dtos = found.Select(f => new DeliverySourceChangeDto(
            f.Change.Kind,
            f.Change.AtUtc,
            f.Change.FileName,
            f.Change.RowNumber,
            f.Loading is { } loading ? Dto(loading) : null,
            f.Landing is { } landing
                ? new DeliveryChainLandingDto(Dto(landing.Run), landing.File.Name, landing.File.Path, landing.File.Rows, landing.File.SizeBytes, landing.File.Modified)
                : null)).ToList();
        var note = sourceTable is null
            ? "The ingestion table this record's flow reads is not recorded by the repository sync, so the ingestion runs that wrote its changes cannot be named."
            : null;
        return new DeliveryRecordChainDto(sourceTable, record.SourceInsertedUtc, dtos, truncated, note);
    }

    /// <summary>One change before its runs are found.</summary>
    private sealed record Change(string Kind, DateTime AtUtc, string? FileName, long? RowNumber);

    /// <summary>A catalog run, as much of it as a chain shows.</summary>
    private sealed record RunRow(
        Guid RunId, Guid PipelineId, string FlowName, string FlowKind, string Status, bool Success, DateTime? StartedUtc,
        DateTime RanUtc, double? DurationSeconds, long Rows, string? Error);

    /// <summary>
    /// The record's changes, newest first: one per distinct stamp the ledger recorded (on the record, delivered or queued,
    /// or on any attempt), its arrival, and each deletion. The arrival is the version stamped at the insert moment, or a
    /// change of its own at that moment when the row changed before any plan read it; without an insert moment, the
    /// earliest version is only the earliest. Past <see cref="MaxChanges"/>, the newest are kept with the arrival.
    /// </summary>
    private static (List<Change> Changes, bool Truncated) Changes(RecordState record, IReadOnlyList<RecordOriginSeen> seen)
    {
        // A stamp names one state of the row; the file and row it came from are taken where the ledger recorded one.
        var versions = new SortedDictionary<DateTime, Change>();
        void Add(RecordOrigin origin)
        {
            if (origin.UpdatedUtc is not { } stamp)
            {
                return;
            }

            if (!versions.TryGetValue(stamp, out var held) || (held.FileName is null && origin.FileName is not null))
            {
                versions[stamp] = new Change(SourceChangeKind.Changed, stamp, origin.FileName, origin.RowNumber);
            }
        }

        foreach (var version in seen)
        {
            Add(version.Origin);
        }

        Add(record.Origin);
        Add(record.PendingOrigin);

        var ordered = versions.Values.ToList();
        if (record.SourceInsertedUtc is { } arrived)
        {
            var kind = ordered.Exists(v => v.AtUtc < arrived) ? SourceChangeKind.Reloaded : SourceChangeKind.Loaded;
            var at = ordered.FindIndex(v => v.AtUtc == arrived);
            if (at >= 0)
            {
                ordered[at] = ordered[at] with { Kind = kind };
            }
            else
            {
                // The row changed before any plan read it: the ledger holds its arrival, not the file that brought it.
                ordered.Add(new Change(kind, arrived, null, null));
            }
        }
        else if (ordered.Count > 0)
        {
            ordered[0] = ordered[0] with { Kind = SourceChangeKind.Earliest };
        }

        ordered.AddRange(seen
            .Where(s => s.DeletedUtc is not null)
            .GroupBy(s => s.DeletedUtc!.Value)
            .Select(g => new Change(SourceChangeKind.Deleted, g.Key, g.First().Origin.FileName, g.First().Origin.RowNumber)));

        var newest = ordered.OrderByDescending(c => c.AtUtc).ThenBy(c => c.Kind == SourceChangeKind.Deleted ? 0 : 1).ToList();
        if (newest.Count <= MaxChanges)
        {
            return (newest, false);
        }

        var arrival = newest.FindLast(c => c.Kind is SourceChangeKind.Loaded or SourceChangeKind.Reloaded or SourceChangeKind.Earliest);
        var kept = newest.Take(MaxChanges - (arrival is null ? 0 : 1)).ToList();
        if (arrival is not null && !kept.Contains(arrival))
        {
            kept.Add(arrival);
        }

        return (kept, true);
    }

    /// <summary>
    /// The pipelines whose flows write <paramref name="sourceTable"/>, from the lineage every flow kind declares. None
    /// without a table, or for a name that is not three parts, which lineage cannot place.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> WritersAsync(CatalogDbContext catalog, string? sourceTable, CancellationToken ct)
    {
        if (sourceTable is null || ObjectSuffix(sourceTable) is not { } suffix)
        {
            return [];
        }

        return await catalog.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId != null && (e.Relation == "Writes" || e.Relation == "Creates") && e.ObjectKey.EndsWith(suffix))
            .Select(e => e.PipelineId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The run of a writer that was executing at <paramref name="stampedUtc"/>: of each writer, the first run recorded at
    /// or after that moment (a run is recorded when it ends, so it is the one that can have been executing then), when it
    /// had started by then. One seek of the catalog's (PipelineId, WrittenUtc) index per writer, however many runs the flow
    /// has made. With no such run, there is none: the table's latest load is not a stand-in.
    /// </summary>
    private static async Task<RunRow?> LoadingRunAsync(CatalogDbContext catalog, IReadOnlyList<Guid> writers, DateTime stampedUtc, CancellationToken ct)
    {
        RunRow? found = null;
        foreach (var pipelineId in writers)
        {
            var run = await catalog.Runs.AsNoTracking()
                .Where(r => r.PipelineId == pipelineId && r.WrittenUtc >= stampedUtc)
                .OrderBy(r => r.WrittenUtc)
                .Select(r => new RunRow(
                    r.RunId, r.PipelineId, r.FlowName, r.FlowKind, r.Status, r.Success, r.StartUtc, r.WrittenUtc, r.DurationSeconds,
                    r.RowsLoaded ?? 0, r.Error))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (run is { StartedUtc: { } started } && started <= stampedUtc && (found is null || started > found.StartedUtc))
            {
                found = run;
            }
        }

        return found;
    }

    /// <summary>
    /// The landing that brought a change's file in: the last successful run that processed a file of that name and was
    /// recorded by <paramref name="beforeUtc"/>, the moment the ingestion run that read it began. A landing after it
    /// carried nothing into this change, however often the file was landed again. A file name the ledger holds with its
    /// path (<c>showPathWithFileName</c>) is matched by its name as well.
    /// </summary>
    private static async Task<(RunRow Run, CatalogRunFile File)?> LandingAsync(CatalogDbContext catalog, string fileName, DateTime beforeUtc, CancellationToken ct)
    {
        var names = new[] { fileName, Path.GetFileName(fileName.Replace('\\', '/')) }.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var landing = await catalog.RunFiles.AsNoTracking()
            .Where(f => names.Contains(f.Name))
            .Join(catalog.Runs.AsNoTracking(), f => f.RunId, r => r.RunId, (f, r) => new { File = f, Run = r })
            .Where(x => x.Run.Success && x.Run.WrittenUtc <= beforeUtc)
            .OrderByDescending(x => x.Run.WrittenUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (landing is null)
        {
            return null;
        }

        var run = landing.Run;
        return (new RunRow(
            run.RunId, run.PipelineId, run.FlowName, run.FlowKind, run.Status, run.Success, run.StartUtc, run.WrittenUtc, run.DurationSeconds,
            landing.File.Rows, run.Error), landing.File);
    }

    /// <summary>
    /// The tail a lineage object key carries for a table: <c>|database|schema|table</c>, folded the way the catalog
    /// folds it. A name that is not three parts (a table named without its database, which lineage cannot place)
    /// matches nothing rather than matching every table of that name.
    /// </summary>
    private static string? ObjectSuffix(string threePartName)
    {
        var parts = threePartName.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var unbracketed = parts.Select(part => part.Trim('[', ']').ToLowerInvariant()).ToList();
        return unbracketed.Exists(string.IsNullOrEmpty) ? null : "|" + string.Join('|', unbracketed);
    }
}
