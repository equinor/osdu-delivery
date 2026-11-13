using System.Globalization;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// How long a partition's cache keeps the records of the versions it no longer renders against
/// (osdu/docs/reference/concepts/partition-cache.md, Retention). A version's records are what a reader browses and compares on
/// the Cache page, and what a delivery flow pinning the version renders against. A delivered record needs none of them once
/// it is delivered: the values its render read are kept with it in its cache set, and a change tag keeps the values before
/// and after. So every refresh and every import that updates a partition's cache also prunes the records of the versions
/// the partition no longer needs, and keeps the records of:
/// <list type="bullet">
/// <item>the current version, which every delivery renders against;</item>
/// <item>the version the current one replaced, which a run that resolved the current version just before it moved may still
/// load, and which the latest change is compared with;</item>
/// <item>every version a delivery flow pins with <c>render.cacheVersion</c>;</item>
/// <item>every other version among the partition's newest (<see cref="DefaultVersions"/> unless an admin sets another
/// number) that a newer one replaced less than the retention ago.</item>
/// </list>
/// A pruned version's row is never removed: it keeps saying who captured the version, when, from where, and what it changed.
/// </summary>
public static class CacheRetention
{
    /// <summary>The retention of a cache flow that declares none, in days.</summary>
    public const int DefaultDays = 7;

    /// <summary>How many versions of a partition's cache keep their records, the current one counted, unless an admin sets another number.</summary>
    public const int DefaultVersions = 3;

    /// <summary>
    /// The fewest versions a partition may keep: the current one and the one it replaced, which are always kept.
    /// </summary>
    public const int MinVersions = 2;

    /// <summary>The most versions a partition may keep.</summary>
    public const int MaxVersions = 1_000;

    /// <summary>Whether <paramref name="versions"/> is a number of versions a partition may keep.</summary>
    public static bool KeepsVersions(int versions) => versions is >= MinVersions and <= MaxVersions;

    /// <summary>Why <paramref name="given"/> is refused as the number of versions a partition keeps, as the API and the CLI say it.</summary>
    public static string VersionsOutOfRange(string given)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"A partition's cache keeps from {MinVersions} to {MaxVersions} versions, the current one counted, not {given}: the current version and the one it replaced always keep their records.");

    /// <summary>
    /// The versions a partition keeps as its row says it, brought into the range an admin may set, so a row edited by hand
    /// cannot stop a retention or keep the versions that are always kept from being kept.
    /// </summary>
    public static int BoundedVersions(int versions) => Math.Clamp(versions, MinVersions, MaxVersions);

    /// <summary>The longest retention a cache flow may declare, in days: a hundred years, longer than any estate keeps a ledger.</summary>
    public const int MaxDays = 36_500;

    /// <summary>The key a cache flow declares its retention under.</summary>
    public const string Key = "retentionDays";

    /// <summary>
    /// The retention a cache flow declares: <paramref name="declared"/>, or <see cref="DefaultDays"/> when it declares none.
    /// </summary>
    /// <exception cref="FlowValidationException">The value is not a whole number of days from 1 to <see cref="MaxDays"/>.</exception>
    public static int Check(int? declared, string source)
    {
        if (declared is not { } days)
        {
            return DefaultDays;
        }

        return days is >= 1 and <= MaxDays
            ? days
            : throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {Key} is {days}, and it is the number of days the partition's cache keeps the records of a version after a newer one replaced it: a whole number from 1 to {MaxDays}. Leave it out to keep them {DefaultDays} days."));
    }

    /// <summary>
    /// The retention of a partition's cache: the longest its synced cache flows declare, or the default when none is synced.
    /// A value outside the range a document may declare is brought into it, so a row edited by hand cannot stop a retention.
    /// </summary>
    public static int OfPartition(IEnumerable<int> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        return declared.Select(Bounded).DefaultIfEmpty(DefaultDays).Max();
    }

    /// <summary>
    /// The retention a refresh or an import by one cache flow applies to its partition: the longest of what the flow
    /// declares and what the partition's other synced cache flows declare, so no flow's history is pruned sooner than it
    /// asked for.
    /// </summary>
    public static int For(int declared, IEnumerable<int> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        var own = Bounded(declared);
        return Math.Max(own, others.Select(Bounded).DefaultIfEmpty(own).Max());
    }

    private static int Bounded(int days) => Math.Clamp(days, 1, MaxDays);

    /// <summary>
    /// Which versions of a partition's cache keep their records at <paramref name="nowUtc"/>, which are pruned now, and which
    /// have their changes recorded first (<see cref="CacheRetentionPlan"/>). A version keeps them while it is among the
    /// newest <paramref name="versionsKept"/>, counted back from the current one, and was replaced less than
    /// <paramref name="retentionDays"/> ago: it stopped being current when the version after it was captured, so that is the
    /// instant its retention runs from. Versions already pruned stay pruned: their records are gone, whatever a pin or a
    /// longer retention says now.
    /// </summary>
    /// <param name="versions">Every version of the partition's cache, in any order.</param>
    /// <param name="retentionDays">
    /// The partition's retention (<see cref="For"/>), or the days an operator's purge keeps; 0 keeps only the versions that
    /// are always kept.
    /// </param>
    /// <param name="nowUtc">The instant the retention is applied at.</param>
    /// <param name="pinned">The version labels active delivery flows pin.</param>
    /// <param name="versionsKept">
    /// How many versions keep their records, the current one counted, from <see cref="MinVersions"/> to
    /// <see cref="MaxVersions"/>: a version older than that many is pruned whatever its days, unless a delivery flow pins it.
    /// </param>
    public static CacheRetentionPlan Plan(
        IReadOnlyList<CacheVersionSpan> versions, int retentionDays, DateTime nowUtc, IReadOnlySet<string> pinned, int versionsKept)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retentionDays, MaxDays);
        ArgumentOutOfRangeException.ThrowIfLessThan(versionsKept, MinVersions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(versionsKept, MaxVersions);

        var ordered = versions.OrderBy(v => v.Sequence).ToList();
        var currentAt = ordered.FindLastIndex(v => v.Current);
        if (currentAt < 0)
        {
            // A cache with no current version has nothing a delivery renders against, so nothing to measure the rest by.
            return CacheRetentionPlan.Nothing;
        }

        var current = ordered[currentAt];

        var cutoff = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) - TimeSpan.FromDays(retentionDays);
        var kept = new List<int>();
        var pruned = new List<CacheVersionSpan>();
        var pins = new List<string>();
        var gone = new HashSet<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var version = ordered[i];
            if (version.Pruned)
            {
                gone.Add(version.Sequence);
                continue;
            }

            // The current version, and any version after it, which a merge never leaves behind the current one.
            if (version.Sequence >= current.Sequence)
            {
                kept.Add(version.Sequence);
                continue;
            }

            var next = ordered[i + 1];
            var replacedCurrent = next.Sequence == current.Sequence;
            // The current version is the newest of those counted, the one it replaced the second, and so on back; a version
            // already pruned still takes its place, since it was one of them.
            var withinRetention = next.CapturedUtc >= cutoff && currentAt - i + 1 <= versionsKept;
            var isPinned = pinned.Contains(version.Version);
            if (replacedCurrent || withinRetention || isPinned)
            {
                kept.Add(version.Sequence);
                if (isPinned && !replacedCurrent && !withinRetention)
                {
                    pins.Add(version.Version);
                }

                continue;
            }

            pruned.Add(version);
            gone.Add(version.Sequence);
        }

        // What a version changed is counted from the rows it began and the rows it ended, and a row it ended belongs to the
        // version before it. So removing a version's rows changes its own counts and those of the kept version after it.
        var counted = new SortedSet<int>(pruned.Select(v => v.Sequence));
        for (var i = 1; i < ordered.Count; i++)
        {
            if (!gone.Contains(ordered[i].Sequence) && gone.Contains(ordered[i - 1].Sequence))
            {
                counted.Add(ordered[i].Sequence);
            }
        }

        return new CacheRetentionPlan(current.Sequence, kept, pruned, pins, [.. counted]);
    }
}

/// <summary>
/// A retention to apply to one partition's cache (<see cref="ICacheStore.ApplyRetentionAsync"/>): the one every refresh and
/// import of a cache flow applies (<see cref="ForFlow"/>), or an operator's purge (<see cref="Purge"/>). Both go through the
/// one pass, so the versions that are always kept are kept either way, and every version pruned records who pruned it.
/// </summary>
public sealed record CacheRetentionRequest
{
    private CacheRetentionRequest(string scope, string? flowName, int days, DateTimeOffset now, string actor, bool dryRun, int? versionsKept)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, CacheRetention.MaxDays);
        if (versionsKept is { } versions && !CacheRetention.KeepsVersions(versions))
        {
            throw new ArgumentOutOfRangeException(
                nameof(versionsKept), versions, CacheRetention.VersionsOutOfRange(versions.ToString(CultureInfo.InvariantCulture)));
        }

        Scope = scope;
        FlowName = flowName;
        Days = days;
        Now = now;
        Actor = actor;
        DryRun = dryRun;
        VersionsKept = versionsKept;
    }

    /// <summary>The partition whose cache the retention applies to.</summary>
    public string Scope { get; }

    /// <summary>
    /// The cache flow whose refresh or import applies the retention, whose declared days are weighed with what the partition's
    /// other cache flows declare (<see cref="CacheDeclaration.RetentionFor"/>); null for an operator's purge.
    /// </summary>
    public string? FlowName { get; }

    /// <summary>
    /// For a cache flow, the retention it declares; for a purge, the days it keeps, exactly, however long the partition's
    /// cache flows ask for.
    /// </summary>
    public int Days { get; }

    /// <summary>The instant the retention is applied at, and recorded on every version it prunes.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>
    /// Who applies it, recorded on every version it prunes: a refresh's requester (a person, or schedule:&lt;name&gt;), the
    /// account an import runs as, or the operator who purged.
    /// </summary>
    public string Actor { get; }

    /// <summary>True to say what the retention would prune, and how many stored rows would go, without pruning anything.</summary>
    public bool DryRun { get; }

    /// <summary>
    /// How many versions keep their records in place of the number the partition keeps; null, as for every refresh and
    /// import, for the partition's own (<see cref="CacheRetention.DefaultVersions"/> unless an admin set another).
    /// </summary>
    public int? VersionsKept { get; }

    /// <summary>
    /// The retention a refresh or an import by <paramref name="flowName"/> applies once its merge is written:
    /// <paramref name="declaredDays"/> is what the flow declares, from 1 to <see cref="CacheRetention.MaxDays"/>.
    /// </summary>
    public static CacheRetentionRequest ForFlow(string scope, string flowName, int declaredDays, DateTimeOffset now, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentOutOfRangeException.ThrowIfLessThan(declaredDays, 1);
        return new CacheRetentionRequest(scope, flowName, declaredDays, now, actor, dryRun: false, versionsKept: null);
    }

    /// <summary>
    /// An operator's purge of the partition's cache history: it keeps the records of every version among the newest
    /// <paramref name="versionsKept"/> (the partition's own number when null) replaced less than <paramref name="keepDays"/>
    /// ago, and of the versions that are always kept; 0 days keeps those alone.
    /// </summary>
    public static CacheRetentionRequest Purge(string scope, int keepDays, DateTimeOffset now, string actor, bool dryRun, int? versionsKept = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepDays);
        return new CacheRetentionRequest(scope, null, keepDays, now, actor, dryRun, versionsKept);
    }
}

/// <summary>A version of a partition's cache as a retention weighs it: its place, its label, when it was captured, and whether it is current or pruned.</summary>
public sealed record CacheVersionSpan(int Sequence, string Version, DateTime CapturedUtc, bool Current, bool Pruned);

/// <summary>
/// What a retention does to a partition's cache: the versions whose records it keeps, the versions it prunes now, and the
/// versions whose change counts it records before any row goes.
/// </summary>
/// <param name="Current">The sequence of the current version; 0 when the cache has none.</param>
/// <param name="Kept">The sequences whose records stay, ascending.</param>
/// <param name="Pruned">The versions pruned now, oldest first.</param>
/// <param name="Pinned">The versions kept only because a delivery flow pins them.</param>
/// <param name="Counted">The sequences whose change counts are recorded before their rows go: every version pruned now, and every kept version right after a pruned one.</param>
public sealed record CacheRetentionPlan(
    int Current, IReadOnlyList<int> Kept, IReadOnlyList<CacheVersionSpan> Pruned, IReadOnlyList<string> Pinned, IReadOnlyList<int> Counted)
{
    /// <summary>The plan for a cache with no current version: nothing kept, nothing pruned.</summary>
    public static CacheRetentionPlan Nothing { get; } = new(0, [], [], [], []);

    /// <summary>
    /// The kept sequences as runs of consecutive versions, ascending: a stored row stays when its range of versions overlaps
    /// any of them.
    /// </summary>
    public IReadOnlyList<(int From, int To)> KeptRuns()
    {
        var runs = new List<(int From, int To)>();
        foreach (var sequence in Kept.Order())
        {
            if (runs.Count > 0 && runs[^1].To == sequence - 1)
            {
                runs[^1] = (runs[^1].From, sequence);
            }
            else
            {
                runs.Add((sequence, sequence));
            }
        }

        return runs;
    }
}

/// <summary>
/// What applying a partition's cache retention did, for the result of the refresh, the import or the purge that applied it.
/// </summary>
/// <param name="RetentionDays">
/// The retention applied: for a refresh or an import, the longest any cache flow of the partition declares; for a purge,
/// the days it keeps.
/// </param>
/// <param name="Kept">How many versions keep their records.</param>
/// <param name="Pruned">The versions whose records this pass pruned (for a dry run, would prune), oldest first.</param>
/// <param name="RowsRemoved">How many stored rows went (for a dry run, would go); a row stays while any kept version holds it.</param>
/// <param name="Failure">
/// Why the retention could not be applied, when it could not. The update of the cache stands either way, and the next
/// refresh or import applies the retention again.
/// </param>
/// <param name="Deferred">
/// Why the retention pruned nothing this time although it may have had something to prune: what it is waiting for. The
/// refresh or import after that applies it.
/// </param>
/// <param name="DryRun">True when the pass only said what it would prune, and pruned nothing.</param>
/// <param name="VersionsKept">The most versions the pass let keep their records, the current one counted, pinned versions aside.</param>
public sealed record CacheRetentionOutcome(
    int RetentionDays, int Kept, IReadOnlyList<string> Pruned, long RowsRemoved, string? Failure = null, string? Deferred = null, bool DryRun = false,
    int VersionsKept = CacheRetention.DefaultVersions)
{
    /// <summary>The retention applied in a few words, as a log line and the CLI name it: <c>3 version(s), 7 day(s)</c>.</summary>
    public string Rule => string.Create(CultureInfo.InvariantCulture, $"{VersionsKept} version(s), {RetentionDays} day(s)");

    /// <summary>What an operator's purge did, or for a dry run would do, in one line: the Cache page, the API and the CLI say it alike.</summary>
    public string DescribePurge()
    {
        var window = RetentionDays == 0
            ? "keeping only the current version, the one it replaced and every pinned version"
            : string.Create(CultureInfo.InvariantCulture, $"keeping the newest {VersionsKept} version(s), none replaced over {RetentionDays} day(s) ago");
        if (Deferred is { } waiting)
        {
            return $"Nothing can be pruned yet: {waiting}.";
        }

        if (Pruned.Count == 0 && RowsRemoved == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Nothing to prune {window}; {Kept} version(s) keep their records.");
        }

        return DryRun
            ? string.Create(CultureInfo.InvariantCulture, $"Pruning, {window}, removes the records of {Pruned.Count} version(s), {RowsRemoved} stored row(s); {Kept} version(s) keep theirs.")
            : string.Create(CultureInfo.InvariantCulture, $"Pruned the records of {Pruned.Count} version(s), {window}, and removed {RowsRemoved} stored row(s); {Kept} version(s) keep theirs.");
    }
}

/// <summary>
/// A version of a partition's cache whose records the cache's retention pruned: it can no longer be read, compared or
/// rendered against, and its row still says what it was.
/// </summary>
public sealed class CacheVersionPrunedException : DeliveryException
{
    public CacheVersionPrunedException(string scope, string version, DateTime prunedUtc, string? prunedBy = null)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"Version {version} of the cache of partition '{scope}' was pruned at {prunedUtc:yyyy-MM-dd HH:mm:ss}Z{(string.IsNullOrWhiteSpace(prunedBy) ? string.Empty : $" by {prunedBy}")}, under the cache's retention: its records are no longer kept, only what the version was (who captured it, when, from where and what it changed). Read the current version, or a version the cache still holds ('sqlflow cache list {scope}' lists them)."))
    {
        Scope = scope;
        Version = version;
        PrunedUtc = prunedUtc;
        PrunedBy = prunedBy;
    }

    /// <summary>The partition whose cache held the version.</summary>
    public string Scope { get; }

    /// <summary>The version's label.</summary>
    public string Version { get; }

    /// <summary>When its records were pruned.</summary>
    public DateTime PrunedUtc { get; }

    /// <summary>Who pruned them; null for a version pruned before that was recorded.</summary>
    public string? PrunedBy { get; }
}
