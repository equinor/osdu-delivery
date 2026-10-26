using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The dimensions of dimension flows (osdu/docs/reference/concepts/dimension-tables.md): a row per
/// dimension of a flow in a partition, a row per build, and the dimension's members, originals and change log. Every
/// table is a ledger table, written under the ledger identity the build registered, so a partition's dimensions are kept
/// together and read in one range of it.
/// </summary>
public sealed partial class OsduLedger
{
    /// <summary>The most lines of notes a build keeps.</summary>
    private const int MaxDimensionNotes = 100;

    /// <summary>The most members or originals one page reads.</summary>
    public const int MaxDimensionPage = 1000;

    public async Task<(DimensionState Dimension, DimensionRunState Run)> StartDimensionRunAsync(
        DimensionDeclaration declaration, Guid? runId, string actor, DateTime startedUtc, string mode = DimensionRunModes.Full, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (mode is not (DimensionRunModes.Full or DimensionRunModes.Incremental))
        {
            throw new ArgumentException($"A build reads its dimension {DimensionRunModes.Full} or {DimensionRunModes.Incremental}, not '{mode}'.", nameof(mode));
        }

        var partition = await WritePartitionAsync(declaration.FlowId, ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            await using var db = Open();
            var dimension = await db.DeliveryDimensions
                .FirstOrDefaultAsync(d => d.PartitionId == partition && d.FlowId == declaration.FlowId && d.Name == declaration.Name, ct).ConfigureAwait(false);
            if (dimension is null)
            {
                dimension = new DeliveryDimension { PartitionId = partition, FlowId = declaration.FlowId, CreatedUtc = startedUtc };
                db.DeliveryDimensions.Add(dimension);
            }

            dimension.FlowName = Truncate(declaration.FlowName, DeliveryLedger.MaxFlowNameLength)!;
            dimension.Name = declaration.Name;
            dimension.Description = Truncate(declaration.Description, 4000);
            dimension.Kind = declaration.Kind;
            dimension.Query = declaration.Query;
            dimension.Path = declaration.Path;
            dimension.CleanJson = declaration.CleanJson;
            dimension.LabelJson = declaration.LabelJson;
            dimension.AttributesJson = declaration.AttributesJson;
            dimension.ElementsJson = declaration.ElementsJson;
            dimension.DefinitionHash = declaration.DefinitionHash;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < 3 && IsDuplicate(ex))
            {
                // Two builds registered the same new dimension at once; the other's row is the one to build under.
                continue;
            }

            var run = new DeliveryDimensionRun
            {
                PartitionId = partition,
                DimensionId = dimension.DimensionId,
                FlowId = declaration.FlowId,
                RunId = runId,
                Actor = Truncate(actor, 200)!,
                Status = DimensionRunStatus.Running,
                Mode = mode,
                DefinitionHash = declaration.DefinitionHash,
                Query = declaration.Query,
                StartedUtc = startedUtc,
            };
            db.DeliveryDimensionRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
            return (ToState(dimension) with { Partition = name }, ToState(run));
        }
    }

    public async Task<DimensionStage> StageDimensionAsync(Guid flowId, int dimensionId, long dimensionRunId, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var stage = new DimensionStage(Open, partition, dimensionId, dimensionRunId, flowId);
        try
        {
            // What an earlier build of the dimension left (its process stopped before it ended) is dropped, unless the build is
            // still running: one that has run for longer than any build takes is taken for one its process left. A build that
            // is writing the dimension holds its write lock, which a drop waits for no moment: what it left stays for a later
            // build.
            foreach (var (build, status, started) in await SqlServerDimensionStage.LeftAsync(Open, stage, ct).ConfigureAwait(false))
            {
                if (status == DimensionRunStatus.Running && started is { } at && Now - at < DimensionStage.AbandonedAfter)
                {
                    continue;
                }

                if (!await SqlServerDimensionStage.DiscardLeftAsync(Open, stage, build, ct).ConfigureAwait(false))
                {
                    break;
                }
            }

            await stage.CreateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await stage.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return stage;
    }

    public async Task<DimensionRunState> WriteDimensionAsync(DimensionWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        foreach (var original in write.Originals)
        {
            if (original.Original.Length > DeliveryDimensionValue.MaxOriginalLength)
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"A key of dimension {write.DimensionId} is {original.Original.Length} characters, more than the {DeliveryDimensionValue.MaxOriginalLength} a dimension keeps; the build leaves such values out before it writes."));
            }
        }

        await using var stage = await StageDimensionAsync(write.FlowId, write.DimensionId, write.DimensionRunId, ct).ConfigureAwait(false);
        try
        {
            await stage.AddValuesAsync(write.Originals.Select((o, seq) => ((long)seq, o)).ToList(), ct).ConfigureAwait(false);
            await stage.AddAttributesAsync(write.Originals.SelectMany(o => (o.Attributes ?? []).Select(a => (o.Original, a))).ToList(), ct).ConfigureAwait(false);
            await stage.AddMembersAsync(write.Members, ct).ConfigureAwait(false);
            await stage.AddTextsAsync(write.CollectedTexts, ct).ConfigureAwait(false);
            await stage.AddReadThroughAsync(write.KeyRecords.ToList(), ct).ConfigureAwait(false);
            await stage.AddGoneAsync(write.Removed, ct).ConfigureAwait(false);
            if (write.Records is { } records)
            {
                await stage.AddRecordsAsync(records.Read, records.Held, ct).ConfigureAwait(false);
            }

            return await WriteDimensionAsync(stage, write.Staged(), ct).ConfigureAwait(false);
        }
        finally
        {
            await stage.DiscardAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<DimensionRunState> WriteDimensionAsync(DimensionStage stage, DimensionStagedWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(write);
        if (write.Table is { } table)
        {
            await GuardTableNameAsync(stage.DimensionId, table, ct).ConfigureAwait(false);
        }

        await stage.FlushAsync(ct).ConfigureAwait(false);
        var partitionName = await PartitionNameAsync(stage.PartitionId, ct).ConfigureAwait(false) ?? string.Empty;

        await using var db = Open();
        DeliveryDimensionRun? closed = null;
        await SqlServerDimensionStore.WriteAsync(db, partitionName, stage, write, (written, run) =>
        {
            run.Status = DimensionRunStatus.Completed;
            run.AggregateBy = write.Field?.AggregateBy;
            Apply(run, write.Read);
            run.Members = written.Members;
            run.Originals = written.Originals;
            run.LeftOut = written.LeftOut;
            run.Unfilterable = written.Unfilterable;
            run.MembersAdded = written.Changes.MembersAdded;
            run.MembersRemoved = written.Changes.MembersRemoved;
            run.MembersRestored = written.Changes.MembersRestored;
            run.OriginalsAdded = written.Changes.OriginalsAdded;
            run.OriginalsRemoved = written.Changes.OriginalsRemoved;
            run.OriginalsMoved = written.Changes.OriginalsMoved;
            run.OriginalsRestored = written.Changes.OriginalsRestored;
            if (written.AttributesChanged > 0)
            {
                var notes = run.Notes is null ? [] : JsonSerializer.Deserialize<List<string>>(run.Notes) ?? [];
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{written.AttributesChanged} attribute value(s) of keys were added, rewritten or dropped."));
                run.Notes = JsonSerializer.Serialize(notes.Take(MaxDimensionNotes).ToList());
            }

            // The build ends when it has written, not when it began to: its duration counts the write.
            run.CompletedUtc = Now < write.CompletedUtc ? write.CompletedUtc : Now;
            run.Error = null;
            closed = run;
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        return ToState(closed!);
    }

    public async Task CloseDimensionRunAsync(long dimensionRunId, string status, DimensionReadCounts read, string? failure, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(read);
        if (status is not (DimensionRunStatus.Failed or DimensionRunStatus.Cancelled))
        {
            throw new ArgumentException($"A build is closed as {DimensionRunStatus.Failed} or {DimensionRunStatus.Cancelled} here; a completed one is closed by the write of its values.", nameof(status));
        }

        await using var db = Open();
        var run = await db.DeliveryDimensionRuns.FirstOrDefaultAsync(r => r.DimensionRunId == dimensionRunId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {dimensionRunId} is not in the ledger."));
        run.Status = status;
        Apply(run, read);
        run.Error = Truncate(failure, 4000);
        run.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteDimensionRunAsync(long dimensionRunId, DimensionReadCounts read, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var run = await db.DeliveryDimensionRuns.FirstOrDefaultAsync(r => r.DimensionRunId == dimensionRunId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {dimensionRunId} is not in the ledger."));
        if (run.Status != DimensionRunStatus.Running)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {dimensionRunId} is {run.Status} already, so it cannot be completed."));
        }

        var dimension = await db.DeliveryDimensions.FirstOrDefaultAsync(d => d.PartitionId == run.PartitionId && d.DimensionId == run.DimensionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension {run.DimensionId} of build {dimensionRunId} is not in the ledger."));

        // Nothing was written, so the build counts what the dimension holds, as its last write left it.
        var held = db.DeliveryDimensionValues.AsNoTracking()
            .Where(v => v.PartitionId == run.PartitionId && v.DimensionId == run.DimensionId && v.RemovedRunId == null);
        var leftOut = await held.LongCountAsync(v => v.MemberId == null, ct).ConfigureAwait(false);
        var unfilterable = await held.LongCountAsync(v => v.MemberId != null && !v.Filterable, ct).ConfigureAwait(false);
        run.Status = DimensionRunStatus.Completed;
        Apply(run, read);
        run.Members = dimension.Members;
        run.Originals = dimension.Originals;
        run.LeftOut = leftOut;
        run.Unfilterable = unfilterable;
        run.AggregateBy = dimension.AggregateBy;
        run.Error = null;
        run.CompletedUtc = completedUtc;
        dimension.LastRunId = run.DimensionRunId;
        dimension.LastBuiltUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> DimensionReadUpToAsync(int dimensionId, CancellationToken ct = default)
    {
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var upTo = await ReadAsync(
            db => db.DeliveryDimensionRuns.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.DimensionId == dimensionId && r.Status == DimensionRunStatus.Completed && r.WindowTo != null)
                .MaxAsync(r => r.WindowTo, ct),
            ct).ConfigureAwait(false);
        return upTo is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null;
    }

    public async Task<IReadOnlyList<string>> DimensionKeyRecordTypesAsync(int dimensionId, CancellationToken ct = default)
    {
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        return await ReadAsync(
            db => db.DeliveryDimensionKeyRecords.AsNoTracking()
                .Where(k => k.PartitionId == partition && k.DimensionId == dimensionId)
                .Select(k => k.EntityType)
                .Distinct()
                .OrderBy(t => t)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DimensionKeysReadThroughAsync(int dimensionId, IReadOnlyCollection<string> recordIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in recordIds.Distinct(StringComparer.Ordinal).Chunk(LookupChunk))
        {
            var keys = await ReadAsync(db => SqlServerDimensionStore.KeysReadThroughAsync(db, partition, dimensionId, chunk, ct), ct).ConfigureAwait(false);
            found.UnionWith(keys);
        }

        return found.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> DimensionRecordKeysAsync(int dimensionId, IReadOnlyCollection<byte[]> recordHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recordHashes);
        if (recordHashes.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in recordHashes.Chunk(LookupChunk))
        {
            found.UnionWith(await ReadAsync(db => SqlServerDimensionStore.RecordKeysAsync(db, partition, dimensionId, chunk, ct), ct).ConfigureAwait(false));
        }

        return found.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DimensionValueState>> DimensionOriginalsAsync(int dimensionId, IReadOnlyCollection<string> originals, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originals);
        if (originals.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<DimensionValueState>(originals.Count);
        foreach (var chunk in originals.Distinct(StringComparer.Ordinal).Where(o => o.Length <= DeliveryDimensionValue.MaxOriginalLength).Chunk(LookupChunk))
        {
            found.AddRange(await ReadAsync(db => SqlServerDimensionStore.OriginalsAsync(db, partition, dimensionId, chunk, ct), ct).ConfigureAwait(false));
        }

        return found;
    }

    public async Task<IReadOnlyList<DimensionState>> ListDimensionsAsync(string? partition, Guid? flowId, CancellationToken ct = default)
    {
        short? partitionId = null;
        if (flowId is { } flow)
        {
            if (await PartitionOfAsync(flow, ct).ConfigureAwait(false) is not { } owning)
            {
                return [];
            }

            partitionId = owning;
        }
        else if (!string.IsNullOrWhiteSpace(partition))
        {
            if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } numbered)
            {
                return [];
            }

            partitionId = numbered;
        }

        var rows = await ReadAsync(
            db =>
            {
                var query = db.DeliveryDimensions.AsNoTracking();
                if (partitionId is { } p)
                {
                    query = query.Where(d => d.PartitionId == p);
                }

                if (flowId is { } f)
                {
                    query = query.Where(d => d.FlowId == f);
                }

                return query.OrderBy(d => d.FlowName).ThenBy(d => d.Name).Take(10_000).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        var named = new List<DimensionState>(rows.Count);
        foreach (var row in rows)
        {
            named.Add(ToState(row) with { Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) });
        }

        return named;
    }

    public async Task<DimensionState?> GetDimensionAsync(int dimensionId, CancellationToken ct = default)
    {
        var row = await ReadAsync(db => db.DeliveryDimensions.AsNoTracking().FirstOrDefaultAsync(d => d.DimensionId == dimensionId, ct), ct).ConfigureAwait(false);
        return row is null ? null : ToState(row) with { Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<DimensionState?> FindDimensionAsync(Guid flowId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var wanted = name.Trim();
        var row = await ReadAsync(
            db => db.DeliveryDimensions.AsNoTracking().FirstOrDefaultAsync(d => d.PartitionId == partition && d.FlowId == flowId && d.Name == wanted, ct),
            ct).ConfigureAwait(false);
        return row is null ? null : ToState(row) with { Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<IReadOnlyList<DimensionRunState>> ListDimensionRunsAsync(int dimensionId, int max, CancellationToken ct = default)
    {
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(max, 1, 500);
        var rows = await ReadAsync(
            db => db.DeliveryDimensionRuns.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.DimensionId == dimensionId)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.DimensionRunId)
                .Take(take)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<IReadOnlyList<DimensionRunState>> LatestDimensionRunsAsync(IReadOnlyCollection<int> dimensionIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dimensionIds);
        var found = new List<DimensionRunState>();
        foreach (var chunk in dimensionIds.Distinct().Chunk(LookupChunk))
        {
            var keys = await ReadAsync(
                db => db.DeliveryDimensions.AsNoTracking().Where(d => chunk.Contains(d.DimensionId)).Select(d => new { d.PartitionId, d.DimensionId }).ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var kept in keys.GroupBy(k => k.PartitionId))
            {
                // The newest build of each dimension, read down its builds by start within the partition it is kept in.
                var partition = kept.Key;
                var ids = kept.Select(k => k.DimensionId).ToList();
                var rows = await ReadAsync(
                    db => db.DeliveryDimensionRuns.AsNoTracking()
                        .Where(r => r.PartitionId == partition && ids.Contains(r.DimensionId))
                        .GroupBy(r => r.DimensionId)
                        .Select(g => g.OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.DimensionRunId).First())
                        .ToListAsync(ct),
                    ct).ConfigureAwait(false);
                found.AddRange(rows.Select(ToState));
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<DimensionRunState>> GetDimensionRunsAsync(IReadOnlyCollection<long> dimensionRunIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dimensionRunIds);
        var found = new List<DimensionRunState>();
        foreach (var chunk in dimensionRunIds.Distinct().Chunk(LookupChunk))
        {
            var rows = await ReadAsync(
                db => db.DeliveryDimensionRuns.AsNoTracking().Where(r => chunk.Contains(r.DimensionRunId)).ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(ToState));
        }

        return found;
    }

    public async Task<IReadOnlyList<DimensionRunState>> DimensionRunsOfAsync(Guid runId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryDimensionRuns.AsNoTracking().Where(r => r.RunId == runId).OrderBy(r => r.DimensionRunId).Take(DimensionFlowLimit).ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    /// <summary>The most builds one platform run makes: a flow's dimensions.</summary>
    private const int DimensionFlowLimit = 1000;

    public async Task<IReadOnlyList<DimensionMemberState>> ListDimensionMembersAsync(int dimensionId, DimensionMemberQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var page = await MemberPageAsync(partition, dimensionId, query, ct).ConfigureAwait(false);
        return await WithMemberAttributesAsync(partition, dimensionId, page, ct).ConfigureAwait(false);
    }

    /// <summary>The most values of one attribute a page of members shows beside each member, the most keys first.</summary>
    public const int MemberAttributeValues = 5;

    /// <summary>
    /// <paramref name="page"/> with the values each attribute holds among each member's keys a build finds now: the most keys
    /// first, at most <see cref="MemberAttributeValues"/> per attribute. One read for the page.
    /// </summary>
    private async Task<IReadOnlyList<DimensionMemberState>> WithMemberAttributesAsync(
        short partition, int dimensionId, IReadOnlyList<DimensionMemberState> page, CancellationToken ct)
    {
        if (page.Count == 0)
        {
            return page;
        }

        var byMember = (await MemberAttributesAsync(partition, dimensionId, page.Select(m => m.MemberId).ToList(), MemberAttributeValues, ct).ConfigureAwait(false))
            .ToDictionary(m => m.MemberId, m => m.Attributes);
        return byMember.Count == 0
            ? page
            : page.Select(m => byMember.TryGetValue(m.MemberId, out var attributes) ? m with { Attributes = attributes } : m).ToList();
    }

    public async Task<IReadOnlyList<DimensionAttributeCoverage>> DimensionAttributeCoverageAsync(int dimensionId, CancellationToken ct = default)
    {
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var names = (await AttributeIdsAsync(partition, dimensionId, ct).ConfigureAwait(false)).ToDictionary(n => n.Value, n => n.Key);
        if (names.Count == 0)
        {
            return [];
        }

        // A value read keeps where it was read; the value a key holding none is given keeps nothing there. Counted over the keys a
        // build finds now, from the index on attribute and value.
        var rows = await ReadAsync(
            db =>
            {
                var keys = db.DeliveryDimensionValues.Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.RemovedRunId == null);
                return db.DeliveryDimensionAttributeValues.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.DimensionId == dimensionId && a.ValueFrom != null)
                    .Join(keys, a => a.ValueId, v => v.ValueId, (a, v) => new { a.AttributeId, a.ValueId, a.Value })
                    .GroupBy(x => x.AttributeId)
                    .Select(g => new { AttributeId = g.Key, Keys = g.Select(x => x.ValueId).Distinct().Count(), Values = g.Select(x => x.Value).Distinct().Count() })
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Where(r => names.ContainsKey(r.AttributeId))
            .Select(r => new DimensionAttributeCoverage(names[r.AttributeId], r.Keys, r.Values))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<DimensionMemberAttributes>> MemberAttributesAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, int perAttribute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (memberIds.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<DimensionMemberAttributes>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            found.AddRange(await MemberAttributesAsync(partition, dimensionId, chunk, perAttribute, ct).ConfigureAwait(false));
        }

        return found;
    }

    /// <summary>
    /// The values each attribute holds among the keys a build finds now of each member of <paramref name="ids"/>, the most
    /// keys first, at most <paramref name="perAttribute"/> per attribute, in one read grouped by member, name and value.
    /// </summary>
    private async Task<IReadOnlyList<DimensionMemberAttributes>> MemberAttributesAsync(
        short partition, int dimensionId, IReadOnlyCollection<long> ids, int perAttribute, CancellationToken ct)
    {
        var chosen = ids.ToList();
        var names = (await AttributeIdsAsync(partition, dimensionId, ct).ConfigureAwait(false)).ToDictionary(n => n.Value, n => n.Key);
        var grouped = await ReadAsync(
            db => db.DeliveryDimensionAttributeValues.AsNoTracking()
                .Where(a => a.PartitionId == partition && a.DimensionId == dimensionId)
                .Join(
                    db.DeliveryDimensionValues.Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.RemovedRunId == null
                        && v.MemberId != null && chosen.Contains(v.MemberId.Value)),
                    a => a.ValueId, v => v.ValueId, (a, v) => new { MemberId = v.MemberId!.Value, a.AttributeId, a.Value })
                .GroupBy(x => new { x.MemberId, x.AttributeId, x.Value })
                .Select(g => new { g.Key.MemberId, g.Key.AttributeId, g.Key.Value, Keys = g.Count() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var rows = grouped.Where(r => names.ContainsKey(r.AttributeId)).Select(r => new { r.MemberId, Name = names[r.AttributeId], r.Value, r.Keys }).ToList();
        var take = Math.Max(1, perAttribute);
        return rows
            .GroupBy(r => r.MemberId)
            .Select(g => new DimensionMemberAttributes(g.Key, g
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .OrderBy(n => n.Key, StringComparer.Ordinal)
                .SelectMany(n => n.OrderByDescending(r => r.Keys).ThenBy(r => r.Value, StringComparer.Ordinal).Take(take))
                .Select(r => new DimensionMemberAttributeValue(r.Name, r.Value, r.Keys))
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// <paramref name="keys"/> narrowed to those holding every match of <paramref name="matches"/>: under the attribute's
    /// number (<paramref name="attributeIds"/>, by name), one of its values, each compared exactly, through the attribute
    /// index. An attribute the dimension has no number for holds no value, so no key holds it.
    /// </summary>
    private static IQueryable<DeliveryDimensionValue> WithAttributes(
        OsduDbContext db, IQueryable<DeliveryDimensionValue> keys, short partition, int dimensionId, IReadOnlyList<DimensionAttributeMatch> matches,
        IReadOnlyDictionary<string, int> attributeIds)
    {
        foreach (var match in matches)
        {
            if (!attributeIds.TryGetValue(match.Name, out var attribute))
            {
                return keys.Where(v => false);
            }

            var wanted = match.Values.Distinct(StringComparer.Ordinal).ToList();
            keys = keys.Where(v => db.DeliveryDimensionAttributeValues.Any(a => a.PartitionId == partition && a.DimensionId == dimensionId && a.ValueId == v.ValueId
                && a.AttributeId == attribute && wanted.Contains(a.Value)));
        }

        return keys;
    }

    private async Task<IReadOnlyList<DimensionMemberState>> MemberPageAsync(short partition, int dimensionId, DimensionMemberQuery query, CancellationToken ct)
    {
        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var attributeIds = query.Attributes is { Count: > 0 } ? await AttributeIdsAsync(partition, dimensionId, ct).ConfigureAwait(false) : [];
        return await ReadAsync(
            db =>
            {
                var members = db.DeliveryDimensionMembers.AsNoTracking().Where(m => m.PartitionId == partition && m.DimensionId == dimensionId);
                if (!query.IncludeRemoved)
                {
                    members = members.Where(m => m.RemovedRunId == null);
                }

                if (query.Attributes is { Count: > 0 } matches)
                {
                    // A member holding a key that holds every attribute asked for: one key, not several between them.
                    var matching = WithAttributes(
                        db, db.DeliveryDimensionValues.Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.RemovedRunId == null),
                        partition, dimensionId, matches, attributeIds);
                    members = members.Where(m => matching.Any(v => v.MemberId == m.MemberId));
                }

                if (search is not null)
                {
                    // A member is found by its clean value or by any original under it, ignoring case: people search by the
                    // spelling they know. The columns compare exactly, so the search asks them in a collation that folds case.
                    // An original no build finds any more finds its member only when removed ones are asked for as well.
                    var removed = query.IncludeRemoved;
                    members = members.Where(m => EF.Functions.Collate(m.Value, DeliveryModel.SearchCollation).Contains(search)
                        || db.DeliveryDimensionValues.Any(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.MemberId == m.MemberId
                            && (removed || v.RemovedRunId == null)
                            && EF.Functions.Collate(v.Original, DeliveryModel.SearchCollation).Contains(search)));
                }

                // Each order pages by the columns it sorts on, so a page is one seek however deep it is; a member whose count
                // moves between two pages is read where it stands when its page is read.
                IOrderedQueryable<DeliveryDimensionMember> ordered;
                if (query.Order == DimensionMemberOrder.Records)
                {
                    if (query.After is { } after)
                    {
                        members = members.Where(m => m.Records < after.Records || (m.Records == after.Records && m.Value.CompareTo(after.Value) > 0));
                    }

                    ordered = members.OrderByDescending(m => m.Records).ThenBy(m => m.Value);
                }
                else
                {
                    if (query.After is { } after)
                    {
                        members = members.Where(m => m.Value.CompareTo(after.Value) > 0);
                    }

                    ordered = members.OrderBy(m => m.Value);
                }

                return ordered.Take(take).Select(m => new DimensionMemberState
                {
                    MemberId = m.MemberId,
                    DimensionId = m.DimensionId,
                    Value = m.Value,
                    Records = m.Records,
                    RecordsExact = m.RecordsExact,
                    Originals = m.Originals,
                    Unfilterable = m.Unfilterable,
                    Filter = m.Filter,
                    FilterParts = m.FilterParts,
                    FirstSeenRunId = m.FirstSeenRunId,
                    FirstSeenUtc = m.FirstSeenUtc,
                    RemovedRunId = m.RemovedRunId,
                    RemovedUtc = m.RemovedUtc,
                }).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionMemberState>> GetDimensionMembersAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, IReadOnlyCollection<string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        ArgumentNullException.ThrowIfNull(values);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<DimensionMemberState>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            found.AddRange(await MembersAsync(partition, dimensionId, m => chunk.Contains(m.MemberId), ct).ConfigureAwait(false));
        }

        foreach (var chunk in values.Distinct(StringComparer.Ordinal).Chunk(LookupChunk))
        {
            found.AddRange(await MembersAsync(partition, dimensionId, m => chunk.Contains(m.Value), ct).ConfigureAwait(false));
        }

        return found.GroupBy(m => m.MemberId).Select(g => g.First()).OrderBy(m => m.Value, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DimensionValueState>> ListDimensionValuesAsync(int dimensionId, DimensionValueQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var page = await ValuePageAsync(partition, dimensionId, query, ct).ConfigureAwait(false);
        if (page.Count == 0)
        {
            return page;
        }

        // Every attribute of the page's keys, in one read by their ids.
        var ids = page.Select(v => v.ValueId).ToList();
        var attributes = (await ReadAsync(
                db => db.DeliveryDimensionAttributeValues.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.DimensionId == dimensionId && ids.Contains(a.ValueId))
                    .Join(
                        db.DeliveryDimensionAttributeNames, a => new { a.PartitionId, a.AttributeId }, n => new { n.PartitionId, n.AttributeId },
                        (a, n) => new { a.ValueId, n.Name, a.Value, a.ValueFrom, a.Records })
                    .ToListAsync(ct),
                ct).ConfigureAwait(false))
            .GroupBy(a => a.ValueId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DimensionAttributeState>)g
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ThenByDescending(a => a.Records ?? 0).ThenBy(a => a.Value, StringComparer.Ordinal)
                .Select(a => new DimensionAttributeState(a.Name, a.Value, a.ValueFrom, a.Records))
                .ToList());
        return attributes.Count == 0
            ? page
            : page.Select(v => attributes.TryGetValue(v.ValueId, out var held) ? v with { Attributes = held } : v).ToList();
    }

    private async Task<IReadOnlyList<DimensionValueState>> ValuePageAsync(short partition, int dimensionId, DimensionValueQuery query, CancellationToken ct)
    {
        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var attributeIds = query.Attributes is { Count: > 0 } ? await AttributeIdsAsync(partition, dimensionId, ct).ConfigureAwait(false) : [];
        return await ReadAsync(
            db =>
            {
                var values = db.DeliveryDimensionValues.AsNoTracking().Where(v => v.PartitionId == partition && v.DimensionId == dimensionId);
                if (!query.IncludeRemoved)
                {
                    values = values.Where(v => v.RemovedRunId == null);
                }

                if (query.MemberId is { } member)
                {
                    values = values.Where(v => v.MemberId == member);
                }

                if (query.LeftOutOnly)
                {
                    values = values.Where(v => v.MemberId == null);
                }

                if (query.MemberIds is { Count: > 0 } memberIds)
                {
                    var ids = memberIds.Distinct().ToList();
                    values = values.Where(v => v.MemberId != null && ids.Contains(v.MemberId.Value));
                }

                if (query.Attributes is { Count: > 0 } matches)
                {
                    values = WithAttributes(db, values, partition, dimensionId, matches, attributeIds);
                }

                if (search is not null)
                {
                    // A key is found by itself or by the label read for it, ignoring case: an id is searched by the name it
                    // stands for as often as by the id.
                    values = values.Where(v => EF.Functions.Collate(v.Original, DeliveryModel.SearchCollation).Contains(search)
                        || (v.Label != null && EF.Functions.Collate(v.Label, DeliveryModel.SearchCollation).Contains(search)));
                }

                IOrderedQueryable<DeliveryDimensionValue> ordered;
                if (query.Order == DimensionValueOrder.Count)
                {
                    if (query.After is { } after)
                    {
                        values = values.Where(v => v.Count < after.Count || (v.Count == after.Count && v.ValueId > after.ValueId));
                    }

                    ordered = values.OrderByDescending(v => v.Count).ThenBy(v => v.ValueId);
                }
                else
                {
                    if (query.After is { } after)
                    {
                        values = values.Where(v => v.ValueId > after.ValueId);
                    }

                    ordered = values.OrderBy(v => v.ValueId);
                }

                return ordered.Take(take)
                    .Select(v => new DimensionValueState
                    {
                        ValueId = v.ValueId,
                        DimensionId = v.DimensionId,
                        Original = v.Original,
                        MemberId = v.MemberId,
                        MemberValue = db.DeliveryDimensionMembers
                            .Where(m => m.PartitionId == v.PartitionId && m.MemberId == v.MemberId).Select(m => m.Value).FirstOrDefault(),
                        LeftOut = v.LeftOut,
                        Note = v.Note,
                        Label = v.Label,
                        LabelFrom = v.LabelFrom,
                        Filter = v.Filter,
                        Count = v.Count,
                        Filterable = v.Filterable,
                        FirstSeenRunId = v.FirstSeenRunId,
                        FirstSeenUtc = v.FirstSeenUtc,
                        MemberSinceRunId = v.MemberSinceRunId,
                        RemovedRunId = v.RemovedRunId,
                        RemovedUtc = v.RemovedUtc,
                    })
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionValueState>> MemberOriginalsAsync(int dimensionId, IReadOnlyCollection<long> memberIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (memberIds.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var found = new List<DimensionValueState>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            var rows = await ReadAsync(
                db => db.DeliveryDimensionValues.AsNoTracking()
                    .Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.RemovedRunId == null && v.MemberId != null && chunk.Contains(v.MemberId.Value))
                    .Join(db.DeliveryDimensionMembers.Where(m => m.PartitionId == partition), v => v.MemberId, m => m.MemberId, (v, m) => new { v, m.Value })
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(r => ToState(r.v) with { MemberValue = r.Value }));
        }

        return found.OrderBy(v => v.MemberValue, StringComparer.Ordinal).ThenBy(v => v.Original, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DimensionValueState>> TopMemberOriginalsAsync(
        int dimensionId, IReadOnlyCollection<long> memberIds, int perMember, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (memberIds.Count == 0 || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(perMember, 1, MaxTopOriginals);
        var found = new List<DimensionValueState>();
        foreach (var chunk in memberIds.Distinct().Chunk(LookupChunk))
        {
            // One read for the page: each member's most common originals, taken beside it, the member's own range of the index.
            var rows = await ReadAsync(
                db => db.DeliveryDimensionMembers.AsNoTracking()
                    .Where(m => m.PartitionId == partition && m.DimensionId == dimensionId && chunk.Contains(m.MemberId))
                    .SelectMany(m => db.DeliveryDimensionValues
                        .Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.MemberId == m.MemberId && v.RemovedRunId == null)
                        .OrderByDescending(v => v.Count).ThenBy(v => v.ValueId)
                        .Take(take)
                        .Select(v => new { v, m.Value }))
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(r => ToState(r.v) with { MemberValue = r.Value }));
        }

        return found;
    }

    /// <summary>The most originals of one member <see cref="TopMemberOriginalsAsync"/> reads.</summary>
    public const int MaxTopOriginals = 50;

    public async Task<IReadOnlyList<DimensionAttributeValueState>> ListDimensionAttributeValuesAsync(
        int dimensionId, DimensionAttributeValueQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Name);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var name = query.Name;
        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var text = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();

        // The other picks narrow the keys; the attribute's own pick does not narrow its own list, so a cascade's list always
        // offers every value the other picks leave, the one picked among them.
        var others = (query.Attributes ?? []).Where(m => !string.Equals(m.Name, name, StringComparison.Ordinal)).ToList();
        var memberIds = query.MemberIds is { Count: > 0 } ids ? ids.Distinct().ToList() : null;
        var attributeIds = await AttributeIdsAsync(partition, dimensionId, ct).ConfigureAwait(false);
        if (!attributeIds.TryGetValue(name, out var attribute))
        {
            // No build has kept a value of the attribute yet, so it holds none.
            return [];
        }

        var rows = await ReadAsync(
            db =>
            {
                var attributes = db.DeliveryDimensionAttributeValues.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.DimensionId == dimensionId && a.AttributeId == attribute);
                if (text is not null)
                {
                    attributes = attributes.Where(a => EF.Functions.Collate(a.Value, DeliveryModel.SearchCollation).Contains(text));
                }

                var keys = db.DeliveryDimensionValues.Where(v => v.PartitionId == partition && v.DimensionId == dimensionId && v.RemovedRunId == null);
                if (memberIds is not null)
                {
                    keys = keys.Where(v => v.MemberId != null && memberIds.Contains(v.MemberId.Value));
                }

                if (others.Count > 0)
                {
                    keys = WithAttributes(db, keys, partition, dimensionId, others, attributeIds);
                }

                // An attribute's values over the keys a build finds now, from the index on attribute and value: a collected value counts
                // the records holding it, any other every record of its keys.
                return attributes
                    .Join(keys, a => a.ValueId, v => v.ValueId, (a, v) => new { a.Value, Records = a.Records ?? v.Count })
                    .GroupBy(x => x.Value)
                    .Select(g => new { Value = g.Key, Keys = g.Count(), Records = g.Sum(x => x.Records) })
                    .OrderByDescending(x => x.Records).ThenBy(x => x.Value)
                    .Take(take)
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(r => new DimensionAttributeValueState(r.Value, r.Keys, r.Records)).ToList();
    }

    public async Task<IReadOnlyList<DimensionCaptureState>> DimensionCapturesAsync(int dimensionId, CancellationToken ct = default)
    {
        if (await GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { Partition: { } partition } dimension)
        {
            return [];
        }

        // The sync keeps a row per type a cache flow declares now, the dimension it captures as "<flow>/<dimension>".
        var captured = $"{dimension.FlowName}/{dimension.Name}";
        var rows = await ReadAsync(
            db => db.DeliveryCacheDefinitions.AsNoTracking()
                .Where(c => c.Origin == CacheOrigins.DimensionText && c.Scope == partition)
                .Select(c => new { c.FlowName, c.Name, c.SourceObject })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows
            .Where(r => string.Equals(r.SourceObject, captured, StringComparison.OrdinalIgnoreCase))
            .Select(r => new DimensionCaptureState(r.FlowName, r.Name, partition))
            .OrderBy(r => r.CacheFlow, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<DimensionRemoved?> RemoveDimensionAsync(int dimensionId, CancellationToken ct = default)
    {
        if (await GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension
            || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        await using var db = Open();
        var counts = await SqlServerDimensionStore.RemoveAsync(db, partition, dimensionId, dimension.Partition ?? string.Empty, ct).ConfigureAwait(false);
        return new DimensionRemoved(
            dimensionId, dimension.Name, dimension.FlowName, dimension.Partition,
            counts.Values, counts.Keys, counts.Builds, counts.Changes, counts.Attributes, counts.Texts, counts.TableDropped);
    }

    public async Task<bool> EnsureDimensionTableAsync(int dimensionId, DimensionTableSpec table, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (await GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension
            || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return false;
        }

        await GuardTableNameAsync(dimensionId, table, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerDimensionStore.EnsureTableAsync(db, partition, dimensionId, dimension.Partition ?? string.Empty, table, ct).ConfigureAwait(false);
    }

    public async Task<DimensionTableShape?> DimensionTableShapeAsync(int dimensionId, CancellationToken ct = default)
    {
        if (await GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension
            || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var table = TableOf(dimension);
        await using var db = Open();
        return new DimensionTableShape(
            DimensionTables.Shown(table.Name), table.KeyColumn, table.ValueColumn,
            await SqlServerDimensionStore.TableAttributesAsync(db, partition, dimensionId, table.Name, ct).ConfigureAwait(false));
    }

    public async Task<DimensionTablePage?> ReadDimensionTableAsync(int dimensionId, DimensionTableQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension
            || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var table = TableOf(dimension);
        await using var db = Open();
        return await SqlServerDimensionStore.ReadTableAsync(db, partition, dimensionId, table, dimension.Partition ?? string.Empty, query, MaxDimensionPage, ct)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DimensionTableRow> StreamDimensionTableAsync(int dimensionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (await GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is not { } dimension
            || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            yield break;
        }

        var table = TableOf(dimension);
        await using var db = Open();
        await foreach (var row in SqlServerDimensionStore.StreamTableAsync(db, partition, dimensionId, table, dimension.Partition ?? string.Empty, ct).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <summary>
    /// The table a build wrote of the dimension, with its key's and its value's columns as the table has them: the names
    /// recorded when the table was last made ready, which are <c>key</c> and <c>value</c> for one no build has named yet.
    /// </summary>
    private static DimensionTableRef TableOf(DimensionState dimension)
        => new(
            dimension.TableName
                ?? throw new DimensionTableMissingException(
                    $"Dimension {dimension.Name} of {dimension.FlowName} has no table yet: it was built before dimensions had one. Run the flow's pipeline to make it."),
            dimension.KeyColumn ?? DimensionColumnNames.KeyRole,
            dimension.ValueColumn ?? DimensionColumnNames.ValueRole);

    /// <summary>
    /// Refuses a table another dimension writes. A table is named after its dimension alone, so a dimension's name is
    /// unique among the flows of a database: a second flow declaring one of the same name (or of a name apart only in
    /// characters a table's name leaves out) would write the first one's table. And a table holds the rows of one
    /// dimension a partition, so a second copy of the same dimension in the same partition (kept under another ledger of
    /// a flow that changed how it names its partition) would write over this one's rows.
    /// </summary>
    private async Task GuardTableNameAsync(int dimensionId, DimensionTableSpec table, CancellationToken ct)
    {
        // Each read seeks its index, the table's name read from the index alone: a scan, or a lookup of another dimension's
        // row, would wait for a build writing that dimension, which holds its row until it has written.
        var name = table.Name;
        var rows = await ReadAsync(
            db => db.DeliveryDimensions.FromSql($"SELECT * FROM [osdu].[Dimension] WITH (FORCESEEK) WHERE [DimensionId] = {dimensionId}")
                .Concat(db.DeliveryDimensions.FromSql($"SELECT * FROM [osdu].[Dimension] WITH (FORCESEEK) WHERE [TableName] = {name}"))
                .AsNoTracking()
                .Select(d => new { d.PartitionId, d.DimensionId, d.FlowName, d.Name, d.TableName })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        if (rows.FirstOrDefault(r => r.DimensionId == dimensionId) is not { } own)
        {
            return;
        }

        foreach (var other in rows.Where(r => r.DimensionId != dimensionId && r.TableName == name))
        {
            if (!string.Equals(other.FlowName, own.FlowName, StringComparison.OrdinalIgnoreCase) || !string.Equals(other.Name, own.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException(
                    $"Dimension {own.Name} of {own.FlowName} would write the table {DimensionTables.Shown(name)}, which dimension {other.Name} of {other.FlowName} writes: a dimension's table is named after the dimension alone, so a dimension's name is unique among the flows of a database. Rename one of them.");
            }

            if (other.PartitionId == own.PartitionId)
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Dimension {own.Name} of {own.FlowName} is kept twice in this partition (dimensions {other.DimensionId} and {own.DimensionId}, under two ledgers of the flow), and both would write the table {DimensionTables.Shown(name)}. Remove the one the flow no longer builds, then run the flow again."));
            }
        }
    }

    /// <summary>
    /// The numbers the attributes of dimension <paramref name="dimensionId"/> are kept under, by name, compared exactly:
    /// what a read asked for an attribute by its name joins its values on.
    /// </summary>
    private async Task<Dictionary<string, int>> AttributeIdsAsync(short partition, int dimensionId, CancellationToken ct)
    {
        var rows = await ReadAsync(
            db => db.DeliveryDimensionAttributeNames.AsNoTracking()
                .Where(n => n.PartitionId == partition && n.DimensionId == dimensionId)
                .Select(n => new { n.Name, n.AttributeId })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Name, r => r.AttributeId, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<DimensionCollectedText>> ListDimensionCollectedTextsAsync(
        int dimensionId, string name, IReadOnlyCollection<string>? values, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (values is { Count: 0 } || await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Max(1, limit);
        var found = new List<DimensionCollectedText>();
        if (!(await AttributeIdsAsync(partition, dimensionId, ct).ConfigureAwait(false)).TryGetValue(name, out var attribute))
        {
            return found;
        }

        // Every text when no value is named; else the texts of the values named, a chunk of them a read, from the index on name and value.
        var chunks = values is null ? new string[]?[] { null } : values.Distinct(StringComparer.Ordinal).Chunk(LookupChunk).Cast<string[]?>();
        foreach (var chunk in chunks)
        {
            var rows = await ReadAsync(
                db =>
                {
                    var texts = db.DeliveryDimensionCollectedTexts.AsNoTracking()
                        .Where(t => t.PartitionId == partition && t.DimensionId == dimensionId && t.AttributeId == attribute);
                    if (chunk is not null)
                    {
                        texts = texts.Where(t => chunk.Contains(t.Value));
                    }

                    return texts.OrderBy(t => t.Value).ThenBy(t => t.Text).Take(take - found.Count)
                        .Select(t => new DimensionCollectedText(name, t.Text, t.Value, t.Records))
                        .ToListAsync(ct);
                },
                ct).ConfigureAwait(false);
            found.AddRange(rows);
            if (found.Count >= take)
            {
                break;
            }
        }

        // SQL Server compares text padded with spaces, so two texts apart only by trailing spaces are ordered here, exactly.
        return found.OrderBy(t => t.Value, StringComparer.Ordinal).ThenBy(t => t.Text, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<DimensionChangeState>> ListDimensionChangesAsync(int dimensionId, DimensionChangeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await DimensionPartitionAsync(dimensionId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(query.Limit, 1, MaxDimensionPage);
        var rows = await ReadAsync(
            db =>
            {
                var changes = db.DeliveryDimensionChanges.AsNoTracking().Where(c => c.PartitionId == partition && c.DimensionId == dimensionId);
                if (query.DimensionRunId is { } run)
                {
                    changes = changes.Where(c => c.DimensionRunId == run);
                }

                if (query.ValueId is { } value)
                {
                    changes = changes.Where(c => c.ValueId == value);
                }

                if (query.MemberId is { } member)
                {
                    changes = changes.Where(c => c.FromMemberId == member || c.ToMemberId == member);
                }

                if (!string.IsNullOrWhiteSpace(query.Change))
                {
                    var kind = query.Change.Trim();
                    changes = changes.Where(c => c.Change == kind);
                }

                if (query.Before is { } cursor)
                {
                    changes = changes.Where(c => c.ChangeId < cursor);
                }

                return changes.OrderByDescending(c => c.ChangeId).Take(take)
                    .Select(c => new
                    {
                        c.ChangeId,
                        c.DimensionRunId,
                        c.ValueId,
                        Original = db.DeliveryDimensionValues.Where(v => v.PartitionId == c.PartitionId && v.ValueId == c.ValueId).Select(v => v.Original).FirstOrDefault(),
                        c.Change,
                        c.FromMemberId,
                        FromValue = db.DeliveryDimensionMembers.Where(m => m.PartitionId == c.PartitionId && m.MemberId == c.FromMemberId).Select(m => m.Value).FirstOrDefault(),
                        c.ToMemberId,
                        ToValue = db.DeliveryDimensionMembers.Where(m => m.PartitionId == c.PartitionId && m.MemberId == c.ToMemberId).Select(m => m.Value).FirstOrDefault(),
                        c.ChangedUtc,
                    })
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(r => new DimensionChangeState(
            r.ChangeId, r.DimensionRunId, r.ValueId, r.Original ?? string.Empty, r.Change, r.FromMemberId, r.FromValue, r.ToMemberId, r.ToValue,
            DateTime.SpecifyKind(r.ChangedUtc, DateTimeKind.Utc))).ToList();
    }

    /// <summary>The partition number dimension <paramref name="dimensionId"/>'s rows are kept under, or null when there is no such dimension.</summary>
    private async Task<short?> DimensionPartitionAsync(int dimensionId, CancellationToken ct)
        => await ReadAsync(
            db => db.DeliveryDimensions.AsNoTracking().Where(d => d.DimensionId == dimensionId).Select(d => (short?)d.PartitionId).FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);

    private Task<List<DimensionMemberState>> MembersAsync(
        short partition, int dimensionId, System.Linq.Expressions.Expression<Func<DeliveryDimensionMember, bool>> which, CancellationToken ct)
        => ReadAsync(
            db => db.DeliveryDimensionMembers.AsNoTracking()
                .Where(m => m.PartitionId == partition && m.DimensionId == dimensionId)
                .Where(which)
                .Select(m => new DimensionMemberState
                {
                    MemberId = m.MemberId,
                    DimensionId = m.DimensionId,
                    Value = m.Value,
                    Records = m.Records,
                    RecordsExact = m.RecordsExact,
                    Originals = m.Originals,
                    Unfilterable = m.Unfilterable,
                    Filter = m.Filter,
                    FilterParts = m.FilterParts,
                    FirstSeenRunId = m.FirstSeenRunId,
                    FirstSeenUtc = m.FirstSeenUtc,
                    RemovedRunId = m.RemovedRunId,
                    RemovedUtc = m.RemovedUtc,
                })
                .ToListAsync(ct),
            ct);

    private static void Apply(DeliveryDimensionRun run, DimensionReadCounts read)
    {
        run.Records = read.Records;
        run.WithValue = read.WithValue;
        run.Nulls = read.Nulls;
        run.TooLong = read.TooLong;
        run.Unreadable = read.Unreadable;
        run.Aggregations = read.Aggregations;
        run.Slices = read.Slices;
        run.Splits = read.Splits;
        run.ScannedSlices = read.ScannedSlices;
        run.ScanPages = read.ScanPages;
        run.ScannedUnits = read.ScannedUnits;
        run.CountQueries = read.CountQueries;
        run.Labelled = read.Labelled;
        run.Unlabelled = read.Unlabelled;
        run.LabelQueries = read.LabelQueries;
        run.Templates = read.Templates;
        run.Notes = read.Notes.Count == 0 ? null : JsonSerializer.Serialize(read.Notes.Take(MaxDimensionNotes).Select(n => Truncate(n, 2000)).ToList());
        run.Mode = read.Mode;
        run.WindowFrom = read.WindowFrom;
        run.WindowTo = read.WindowTo;
        run.ChangedRecords = read.ChangedRecords;
        run.TouchedKeys = read.TouchedKeys;
        run.RecordKey = Truncate(read.RecordKey, DeliveryDimensionRun.MaxRecordKeyLength);
    }

    private static DimensionState ToState(DeliveryDimension d) => new()
    {
        DimensionId = d.DimensionId,
        FlowId = d.FlowId,
        FlowName = d.FlowName,
        Name = d.Name,
        Description = d.Description,
        Kind = d.Kind,
        Query = d.Query,
        Path = d.Path,
        Field = d.FieldIndex is { } index && d.AggregateBy is { } aggregateBy ? new DimensionFieldState(index, d.NestedPath, aggregateBy, d.Repeats) : null,
        CleanJson = d.CleanJson,
        LabelJson = d.LabelJson,
        AttributesJson = d.AttributesJson,
        ElementsJson = d.ElementsJson,
        CollectedJson = d.CollectedJson,
        DefinitionHash = d.DefinitionHash,
        TableName = d.TableName,
        KeyColumn = d.KeyColumn,
        ValueColumn = d.ValueColumn,
        Members = d.Members,
        Originals = d.Originals,
        LastRunId = d.LastRunId,
        LastBuiltUtc = d.LastBuiltUtc is { } built ? DateTime.SpecifyKind(built, DateTimeKind.Utc) : null,
        LastFullRunId = d.LastFullRunId,
        LastFullBuiltUtc = d.LastFullBuiltUtc is { } full ? DateTime.SpecifyKind(full, DateTimeKind.Utc) : null,
        CreatedUtc = DateTime.SpecifyKind(d.CreatedUtc, DateTimeKind.Utc),
    };

    private static DimensionRunState ToState(DeliveryDimensionRun r) => new()
    {
        DimensionRunId = r.DimensionRunId,
        DimensionId = r.DimensionId,
        FlowId = r.FlowId,
        RunId = r.RunId,
        Actor = r.Actor,
        Status = r.Status,
        DefinitionHash = r.DefinitionHash,
        Query = r.Query,
        AggregateBy = r.AggregateBy,
        Read = new DimensionReadCounts
        {
            Records = r.Records,
            WithValue = r.WithValue,
            Nulls = r.Nulls,
            TooLong = r.TooLong,
            Unreadable = r.Unreadable,
            Aggregations = r.Aggregations,
            Slices = r.Slices,
            Splits = r.Splits,
            ScannedSlices = r.ScannedSlices,
            ScanPages = r.ScanPages,
            ScannedUnits = r.ScannedUnits,
            CountQueries = r.CountQueries,
            Labelled = r.Labelled,
            Unlabelled = r.Unlabelled,
            LabelQueries = r.LabelQueries,
            Templates = r.Templates,
            Notes = r.Notes is null ? [] : JsonSerializer.Deserialize<List<string>>(r.Notes) ?? [],
            Mode = r.Mode,
            WindowFrom = r.WindowFrom is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
            WindowTo = r.WindowTo is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
            ChangedRecords = r.ChangedRecords,
            TouchedKeys = r.TouchedKeys,
            RecordKey = r.RecordKey,
        },
        Members = r.Members,
        Originals = r.Originals,
        LeftOut = r.LeftOut,
        Unfilterable = r.Unfilterable,
        Changes = new DimensionChangeCounts(r.MembersAdded, r.MembersRemoved, r.MembersRestored, r.OriginalsAdded, r.OriginalsRemoved, r.OriginalsMoved, r.OriginalsRestored),
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = r.Error,
    };

    private static DimensionValueState ToState(DeliveryDimensionValue v) => new()
    {
        ValueId = v.ValueId,
        DimensionId = v.DimensionId,
        Original = v.Original,
        MemberId = v.MemberId,
        LeftOut = v.LeftOut,
        Note = v.Note,
        Label = v.Label,
        LabelFrom = v.LabelFrom,
        Filter = v.Filter,
        Count = v.Count,
        Filterable = v.Filterable,
        FirstSeenRunId = v.FirstSeenRunId,
        FirstSeenUtc = DateTime.SpecifyKind(v.FirstSeenUtc, DateTimeKind.Utc),
        MemberSinceRunId = v.MemberSinceRunId,
        RemovedRunId = v.RemovedRunId,
        RemovedUtc = v.RemovedUtc is { } removed ? DateTime.SpecifyKind(removed, DateTimeKind.Utc) : null,
    };
}
