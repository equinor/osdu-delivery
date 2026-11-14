using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.ComputeUsage;

/// <summary>A sum of compute usage: how many pieces of work, their wall-clock time and their compute units.</summary>
public sealed record ComputeUsageTotals(long Work, long WallClockMs, double ComputeUnits);

/// <summary>One UTC day's compute usage.</summary>
public sealed record ComputeUsageOfDay(DateOnly Day, long Work, long WallClockMs, double ComputeUnits);

/// <summary>
/// One flow's compute usage in a period, under the team it was charged to: the smallest part the usage is divided into.
/// A flow whose team changed in the period has a share under each.
/// </summary>
public sealed record ComputeUsageShare(string Team, string Repo, string Project, string Flow, string Kind, long Work, long WallClockMs, double ComputeUnits);

/// <summary>
/// The compute usage of a period: its total, each of its days, and each flow's share, counted from the work the nodes
/// measured and from the daily totals older work was folded into. <see cref="WorkKeptFromUtc"/> is when the oldest piece
/// of work still listed one by one ended; null when none is.
/// </summary>
public sealed record ComputeUsageSummary(
    DateOnly From,
    DateOnly To,
    ComputeUsageTotals Totals,
    IReadOnlyList<ComputeUsageOfDay> Days,
    IReadOnlyList<ComputeUsageShare> Shares,
    DateTime? WorkKeptFromUtc);

/// <summary>
/// Which pieces of work a list asks for: a period, and optionally one team, repository, project folder or flow. A filter
/// left null is not applied; an empty one asks for the work that names none (no team, no repository, no project folder).
/// </summary>
public sealed record ComputeWorkQuery(DateOnly From, DateOnly To, string? Team = null, string? Repo = null, string? Project = null, string? Flow = null);

/// <summary>One piece of work as its row holds it, with every value its compute units were worked out from.</summary>
public sealed record ComputeWorkRow(
    long UsageId,
    string Node,
    string Kind,
    Guid WorkId,
    int Attempt,
    string Team,
    string Repo,
    string Project,
    string Flow,
    string? RequestedBy,
    DateTime StartedUtc,
    DateTime EndedUtc,
    long WallClockMs,
    int Cores,
    int Slots,
    int NodeSlots,
    double ComputeUnits,
    string Outcome);

/// <summary>A page of work, newest first, and where the next page starts; null when this is the last.</summary>
public sealed record ComputeWorkPage(IReadOnlyList<ComputeWorkRow> Rows, string? Next);

/// <summary>
/// The compute usage the nodes measured (osdu/docs/reference/concepts/compute-usage.md): the work each node writes as it
/// ends (<c>osdu.ComputeUsage</c>), the daily totals a cleanup folds old work into (<c>osdu.ComputeUsageDay</c>), and the
/// sums the Compute usage page reads over both. A piece of work is in exactly one of the two tables at any moment, since a
/// fold removes a row and adds it to its day in one transaction, so a sum over both counts it once.
/// </summary>
public sealed class ComputeUsageStore
{
    /// <summary>The longest period a summary or a list covers, in days.</summary>
    public const int MaxPeriodDays = 1100;

    /// <summary>The most pieces of work a page lists.</summary>
    public const int MaxPageSize = 500;

    /// <summary>How many rows one fold statement takes: one short transaction each.</summary>
    internal const int FoldBatch = 5000;

    private readonly Func<OsduDbContext>? _contexts;

    /// <param name="contexts">The module database, or null when the host was started without one: nothing is recorded then, and reading says so.</param>
    public ComputeUsageStore(Func<OsduDbContext>? contexts) => _contexts = contexts;

    /// <summary>Whether this host has a module database to record usage in.</summary>
    public bool Available => _contexts is not null;

    private OsduDbContext Open()
        => (_contexts ?? throw new FlowValidationException(
            $"Compute usage is kept in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}"))();

    /// <summary>
    /// Writes the work a node measured, each measurement once: one already written (the same batch sent again after an
    /// answer was lost) is left as it is. Each row is charged to the team its flow's folder belongs to now, as the
    /// repository's last sync recorded it (<see cref="TeamOf"/>). Returns how many rows were added.
    /// </summary>
    public async Task<int> RecordAsync(IReadOnlyList<ComputeWork> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Count == 0)
        {
            return 0;
        }

        await using var db = Open();
        var ids = work.Select(w => w.MeasurementId).Distinct().ToList();
        var written = await db.DeliveryComputeUsages.AsNoTracking()
            .Where(u => ids.Contains(u.MeasurementId))
            .Select(u => u.MeasurementId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var skip = written.ToHashSet();

        var repoIds = work.Where(w => w.RepoId is not null).Select(w => w.RepoId!.Value).Distinct().ToList();
        var teams = new Dictionary<Guid, List<(string Folder, string Team)>>();
        if (repoIds.Count > 0)
        {
            var named = await db.DeliveryRepositoryTeams.AsNoTracking()
                .Where(t => repoIds.Contains(t.RepoId))
                .Select(t => new { t.RepoId, t.Folder, t.Team })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var t in named)
            {
                if (!teams.TryGetValue(t.RepoId, out var folders))
                {
                    teams[t.RepoId] = folders = [];
                }

                folders.Add((t.Folder, t.Team));
            }
        }

        foreach (var w in work)
        {
            if (!skip.Add(w.MeasurementId))
            {
                continue;
            }

            db.DeliveryComputeUsages.Add(new DeliveryComputeUsage
            {
                MeasurementId = w.MeasurementId,
                Node = w.Node,
                Kind = w.Kind,
                WorkId = w.WorkId,
                Attempt = w.Attempt,
                Team = w.RepoId is { } repoId && teams.TryGetValue(repoId, out var ofRepo) ? TeamOf(w.FlowFolder, ofRepo) : string.Empty,
                RepoId = w.RepoId,
                Repo = w.Repo,
                Project = w.Project,
                Flow = w.Flow,
                RequestedBy = w.RequestedBy,
                StartedUtc = w.StartedUtc,
                EndedUtc = w.EndedUtc,
                WallClockMs = w.WallClockMs,
                Cores = w.Cores,
                Slots = w.Slots,
                NodeSlots = w.NodeSlots,
                ComputeUnits = w.Units,
                Outcome = w.Outcome,
            });
        }

        return await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The team of a flow in <paramref name="flowFolder"/>, among the folders of its repository that name one: the team of
    /// the folder itself, else of the nearest folder above it, the way a run finds its repository document; empty when
    /// none above it names a team. Folder names are matched exactly.
    /// </summary>
    internal static string TeamOf(string flowFolder, IEnumerable<(string Folder, string Team)> named)
    {
        ArgumentNullException.ThrowIfNull(flowFolder);
        ArgumentNullException.ThrowIfNull(named);
        var nearest = -1;
        var team = string.Empty;
        foreach (var (folder, of) in named)
        {
            var holds = folder.Length == 0
                || string.Equals(flowFolder, folder, StringComparison.Ordinal)
                || (flowFolder.Length > folder.Length && flowFolder[folder.Length] == '/' && flowFolder.StartsWith(folder, StringComparison.Ordinal));
            if (holds && folder.Length > nearest)
            {
                nearest = folder.Length;
                team = of;
            }
        }

        return team;
    }

    /// <summary>
    /// The usage of the days <paramref name="from"/> to <paramref name="to"/>, both included, by the UTC day each piece of
    /// work ended on.
    /// </summary>
    /// <exception cref="FlowValidationException">The period ends before it starts, or is longer than <see cref="MaxPeriodDays"/> days.</exception>
    public async Task<ComputeUsageSummary> SummaryAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (fromUtc, toUtc) = Period(from, to);
        await using var db = Open();
        var work = db.DeliveryComputeUsages.AsNoTracking().Where(u => u.EndedUtc >= fromUtc && u.EndedUtc < toUtc);
        var folded = db.DeliveryComputeUsageDays.AsNoTracking().Where(d => d.Day >= from && d.Day <= to);

        var days = new Dictionary<DateOnly, (long Work, long Ms, double Units)>();
        var workDays = await work
            .GroupBy(u => u.EndedUtc.Date)
            .Select(g => new { Day = g.Key, Work = g.LongCount(), Ms = g.Sum(u => u.WallClockMs), Units = g.Sum(u => u.ComputeUnits) })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var d in workDays)
        {
            days[DateOnly.FromDateTime(d.Day)] = (d.Work, d.Ms, d.Units);
        }

        var foldedDays = await folded
            .GroupBy(d => d.Day)
            .Select(g => new { Day = g.Key, Work = g.Sum(d => d.WorkCount), Ms = g.Sum(d => d.WallClockMs), Units = g.Sum(d => d.ComputeUnits) })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var d in foldedDays)
        {
            var has = days.GetValueOrDefault(d.Day);
            days[d.Day] = (has.Work + d.Work, has.Ms + d.Ms, has.Units + d.Units);
        }

        var shares = new Dictionary<(string Team, string Repo, string Project, string Flow, string Kind), (long Work, long Ms, double Units)>();
        var workShares = await work
            .GroupBy(u => new { u.Team, u.Repo, u.Project, u.Flow, u.Kind })
            .Select(g => new { g.Key.Team, g.Key.Repo, g.Key.Project, g.Key.Flow, g.Key.Kind, Work = g.LongCount(), Ms = g.Sum(u => u.WallClockMs), Units = g.Sum(u => u.ComputeUnits) })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var s in workShares)
        {
            shares[(s.Team, s.Repo, s.Project, s.Flow, s.Kind)] = (s.Work, s.Ms, s.Units);
        }

        var foldedShares = await folded
            .GroupBy(d => new { d.Team, d.Repo, d.Project, d.Flow, d.Kind })
            .Select(g => new { g.Key.Team, g.Key.Repo, g.Key.Project, g.Key.Flow, g.Key.Kind, Work = g.Sum(d => d.WorkCount), Ms = g.Sum(d => d.WallClockMs), Units = g.Sum(d => d.ComputeUnits) })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var s in foldedShares)
        {
            var key = (s.Team, s.Repo, s.Project, s.Flow, s.Kind);
            var has = shares.GetValueOrDefault(key);
            shares[key] = (has.Work + s.Work, has.Ms + s.Ms, has.Units + s.Units);
        }

        var keptFrom = await db.DeliveryComputeUsages.AsNoTracking()
            .OrderBy(u => u.EndedUtc)
            .Select(u => (DateTime?)u.EndedUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new ComputeUsageSummary(
            from,
            to,
            new ComputeUsageTotals(days.Values.Sum(d => d.Work), days.Values.Sum(d => d.Ms), days.Values.Sum(d => d.Units)),
            [.. days.OrderBy(d => d.Key).Select(d => new ComputeUsageOfDay(d.Key, d.Value.Work, d.Value.Ms, d.Value.Units))],
            [.. shares
                .OrderByDescending(s => s.Value.Units)
                .ThenBy(s => s.Key.Team, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Key.Repo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Key.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Key.Flow, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Key.Kind, StringComparer.Ordinal)
                .Select(s => new ComputeUsageShare(s.Key.Team, s.Key.Repo, s.Key.Project, s.Key.Flow, s.Key.Kind, s.Value.Work, s.Value.Ms, s.Value.Units))],
            keptFrom is { } kept ? DateTime.SpecifyKind(kept, DateTimeKind.Utc) : null);
    }

    /// <summary>
    /// The pieces of work of a period still listed one by one, newest first, at most <paramref name="take"/> of them after
    /// <paramref name="after"/> (a page's <see cref="ComputeWorkPage.Next"/>; null for the first page).
    /// </summary>
    /// <exception cref="FlowValidationException">The period, the page size or the continuation is not one this list takes.</exception>
    public async Task<ComputeWorkPage> WorkAsync(ComputeWorkQuery query, string? after, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (take is < 1 or > MaxPageSize)
        {
            throw new FlowValidationException($"A page of work holds 1 to {MaxPageSize} rows; {take} was asked for.");
        }

        var (fromUtc, toUtc) = Period(query.From, query.To);
        await using var db = Open();
        var rows = db.DeliveryComputeUsages.AsNoTracking().Where(u => u.EndedUtc >= fromUtc && u.EndedUtc < toUtc);
        if (query.Team is { } team)
        {
            rows = rows.Where(u => u.Team == team);
        }

        if (query.Repo is { } repo)
        {
            rows = rows.Where(u => u.Repo == repo);
        }

        if (query.Project is { } project)
        {
            rows = rows.Where(u => u.Project == project);
        }

        if (query.Flow is { } flow)
        {
            rows = rows.Where(u => u.Flow == flow);
        }

        if (after is not null)
        {
            var (endedUtc, usageId) = ParseContinuation(after);
            rows = rows.Where(u => u.EndedUtc < endedUtc || (u.EndedUtc == endedUtc && u.UsageId < usageId));
        }

        var page = await rows
            .OrderByDescending(u => u.EndedUtc)
            .ThenByDescending(u => u.UsageId)
            .Take(take + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var more = page.Count > take;
        if (more)
        {
            page.RemoveAt(page.Count - 1);
        }

        var last = page.Count > 0 ? page[^1] : null;
        return new ComputeWorkPage(
            [.. page.Select(u => new ComputeWorkRow(
                u.UsageId, u.Node, u.Kind, u.WorkId, u.Attempt, u.Team, u.Repo, u.Project, u.Flow, u.RequestedBy,
                DateTime.SpecifyKind(u.StartedUtc, DateTimeKind.Utc), DateTime.SpecifyKind(u.EndedUtc, DateTimeKind.Utc),
                u.WallClockMs, u.Cores, u.Slots, u.NodeSlots, u.ComputeUnits, u.Outcome))],
            more && last is not null ? Continuation(last.EndedUtc, last.UsageId) : null);
    }

    /// <summary>How many pieces of work ended before <paramref name="olderThanUtc"/> and are still listed one by one: what <see cref="FoldAsync(DateTime, CancellationToken)"/> would fold.</summary>
    public async Task<long> CountFoldableAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        return await db.DeliveryComputeUsages.AsNoTracking().LongCountAsync(u => u.EndedUtc < olderThanUtc, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds every piece of work that ended before <paramref name="olderThanUtc"/> into the total of its day, team and flow
    /// and removes its row, oldest first, <see cref="FoldBatch"/> rows a transaction: a row leaves the work table in the same
    /// transaction that adds it to its day, so no reader ever counts it twice or not at all, and a fold that is interrupted
    /// or runs twice at once folds each row once. Returns how many rows it folded.
    /// </summary>
    public Task<long> FoldAsync(DateTime olderThanUtc, CancellationToken ct = default) => FoldAsync(olderThanUtc, FoldBatch, ct);

    internal async Task<long> FoldAsync(DateTime olderThanUtc, int batch, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);

        // The rows leave through OUTPUT into a table variable, and the day totals are added from it, in one transaction.
        // The upsert holds its key range (HOLDLOCK), so two folds adding the same new day cannot both insert it.
        const string sql = """
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            DECLARE @folded TABLE (
                [Day] date NOT NULL,
                [Team] nvarchar(64) NOT NULL,
                [Repo] nvarchar(256) NOT NULL,
                [Project] nvarchar(100) NOT NULL,
                [Flow] nvarchar(400) NOT NULL,
                [Kind] nvarchar(8) NOT NULL,
                [WallClockMs] bigint NOT NULL,
                [ComputeUnits] float NOT NULL);
            BEGIN TRANSACTION;
            WITH old AS (
                SELECT TOP (@take) *
                FROM [osdu].[ComputeUsage]
                WHERE [EndedUtc] < @olderThan
                ORDER BY [EndedUtc], [UsageId])
            DELETE FROM old
            OUTPUT CAST(deleted.[EndedUtc] AS date), deleted.[Team], deleted.[Repo], deleted.[Project], deleted.[Flow], deleted.[Kind],
                   deleted.[WallClockMs], deleted.[ComputeUnits]
            INTO @folded ([Day], [Team], [Repo], [Project], [Flow], [Kind], [WallClockMs], [ComputeUnits]);
            MERGE [osdu].[ComputeUsageDay] WITH (HOLDLOCK) AS d
            USING (
                SELECT [Day], [Team], [Repo], [Project], [Flow], [Kind],
                       COUNT_BIG(*) AS [WorkCount], SUM([WallClockMs]) AS [WallClockMs], SUM([ComputeUnits]) AS [ComputeUnits]
                FROM @folded
                GROUP BY [Day], [Team], [Repo], [Project], [Flow], [Kind]) AS f
               ON d.[Day] = f.[Day] AND d.[Team] = f.[Team] AND d.[Repo] = f.[Repo] AND d.[Project] = f.[Project]
              AND d.[Flow] = f.[Flow] AND d.[Kind] = f.[Kind]
            WHEN MATCHED THEN
                UPDATE SET [WorkCount] = d.[WorkCount] + f.[WorkCount],
                           [WallClockMs] = d.[WallClockMs] + f.[WallClockMs],
                           [ComputeUnits] = d.[ComputeUnits] + f.[ComputeUnits]
            WHEN NOT MATCHED THEN
                INSERT ([Day], [Team], [Repo], [Project], [Flow], [Kind], [WorkCount], [WallClockMs], [ComputeUnits])
                VALUES (f.[Day], f.[Team], f.[Repo], f.[Project], f.[Flow], f.[Kind], f.[WorkCount], f.[WallClockMs], f.[ComputeUnits]);
            COMMIT TRANSACTION;
            SELECT COUNT_BIG(*) FROM @folded;
            """;

        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var command = ((SqlConnection)db.Database.GetDbConnection()).CreateCommand();
                command.CommandText = sql;
                command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = batch });
                command.Parameters.Add(new SqlParameter("@olderThan", SqlDbType.DateTime2) { Value = olderThanUtc });
                var folded = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                total += folded;
                if (folded < batch)
                {
                    return total;
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The period's first instant and the instant after its last day, checked.</summary>
    private static (DateTime FromUtc, DateTime ToUtc) Period(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new FlowValidationException($"The period ends ({to:yyyy-MM-dd}) before it starts ({from:yyyy-MM-dd}).");
        }

        if (to.DayNumber - from.DayNumber >= MaxPeriodDays)
        {
            throw new FlowValidationException($"A period covers at most {MaxPeriodDays} days; {from:yyyy-MM-dd} to {to:yyyy-MM-dd} is {to.DayNumber - from.DayNumber + 1}.");
        }

        return (from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private static string Continuation(DateTime endedUtc, long usageId)
        => string.Create(CultureInfo.InvariantCulture, $"{endedUtc.Ticks}-{usageId}");

    private static (DateTime EndedUtc, long UsageId) ParseContinuation(string after)
    {
        var dash = after.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0
            && long.TryParse(after.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && long.TryParse(after.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var usageId)
            && ticks <= DateTime.MaxValue.Ticks)
        {
            return (new DateTime(ticks, DateTimeKind.Utc), usageId);
        }

        throw new FlowValidationException($"'{after}' is not where a page of work continues; pass the value the page before answered with.");
    }
}
