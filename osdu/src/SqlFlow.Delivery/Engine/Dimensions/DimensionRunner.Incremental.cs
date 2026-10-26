using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A dimension's incremental load (osdu/docs/reference/flow/dimension-incremental.md, Full and incremental loads). The flow's
/// <c>incremental</c> block names a record's unique key (<c>keyColumns</c>) and when a record last changed
/// (<c>dateColumns</c>, by default OSDU's own <c>modifyTime</c> and, for a record never modified, its
/// <c>createTime</c>); a nested object changes only with its record. A full load keeps, by each record's unique key,
/// the keys it holds. An incremental load reads the records that changed in its window, and reads again whole over the
/// dimension's query (its count, label, attributes, collected values and elements) every key one of them holds now or
/// held before, and every key whose label or attributes were read through a record that changed; a key it reads again
/// and finds held by no record is removed. Every other key stays as the last load left it. A record that left the index
/// cannot be seen, so the keys only it held, and the counts it made, stay until a full load: when the flow's
/// <c>fullLoadAfterHours</c> says, or a run asks for one.
/// </summary>
public sealed partial class DimensionRunner
{
    /// <summary>The share of a dimension's keys or records, one in this many, past which reading every key costs less than reading those that changed.</summary>
    private const int FullLoadShare = 4;

    /// <summary>
    /// The most records of one entity type the dimension's keys were read through that an incremental load looks up as
    /// changed; past it, reading every key costs less than finding which to read again.
    /// </summary>
    private const int MaxChangedReferences = 100_000;

    /// <summary>
    /// The most records an incremental load reads as changed: it holds them while it finds what to read, so past it a full
    /// load, which reads a range at a time and holds no more however large the dimension, reads it instead.
    /// </summary>
    internal int MaxIncrementalRecords { get; init; } = 250_000;

    /// <summary>The most keys an incremental load reads again, which it holds as it does its changed records (<see cref="MaxIncrementalRecords"/>).</summary>
    internal int MaxIncrementalKeys { get; init; } = 250_000;

    /// <summary>
    /// How a build loads one dimension: <see cref="Mode"/>, the window an incremental load reads (<c>[From, To)</c>), why a
    /// flow loading incrementally reads it in full, and <see cref="FullTo"/>, up to when a full load counts as having read
    /// (its start less the lag), which the next incremental load reads on from.
    /// </summary>
    private sealed record LoadPlan(string Mode, DateTime? From, DateTime To, DateTime FullTo, string? FullBecause)
    {
        /// <summary>The dimension as the ledger held it before the build; null when the build loads it in full.</summary>
        public DimensionState? Existing { get; init; }

        /// <summary>The dimension's last full load; null when the build loads it in full.</summary>
        public DimensionRunState? LastFull { get; init; }

        /// <summary>The same build, loading in full since <paramref name="why"/>.</summary>
        public LoadPlan InFull(string why) => this with { Mode = DimensionRunModes.Full, From = null, FullBecause = why };

        /// <summary><paramref name="read"/> as this load records it: how it read, its window, what it found changed and read again, and why it read in full.</summary>
        public DimensionReadCounts Stamp(DimensionReadCounts read, long? changed = null, long? touched = null)
        {
            var incremental = Mode == DimensionRunModes.Incremental;
            var why = FullBecause is null ? null : $"Loaded in full, since {FullBecause}.";
            return read with
            {
                Mode = Mode,
                WindowFrom = incremental ? From : null,
                WindowTo = incremental ? To : FullTo,
                ChangedRecords = incremental ? changed : null,
                TouchedKeys = incremental ? touched : null,
                Notes = why is null || (read.Notes.Count > 0 && read.Notes[0] == why) ? read.Notes : [why, .. read.Notes],
            };
        }
    }

    /// <summary>What an incremental load came to: the build, written; or why the same build loads the dimension in full instead.</summary>
    private sealed record IncrementalOutcome(DimensionBuildSummary? Summary, string? FullBecause)
    {
        public static IncrementalOutcome InFull(string why) => new(null, why);
    }

    /// <summary>
    /// How a build loads <paramref name="dimension"/>: in full for a flow without an <c>incremental</c> block, when the run or
    /// the block asks for it, and when an incremental load could not be true (the dimension never loaded here; no full load
    /// that kept what its keys were read through and the keys its records hold by the flow's unique key; a declaration or a
    /// query other than its last full load's; a last full load older than <c>fullLoadAfterHours</c>); incrementally
    /// otherwise, from the lag before up to when its completed builds have read, or over the window the run names, to the
    /// build's start less the lag.
    /// </summary>
    private async Task<LoadPlan> PlanLoadAsync(ILedger ledger, DimensionSpec dimension, string? query, DimensionLoadRequest request, DateTime started, CancellationToken ct)
    {
        var incremental = _flow.Incremental;
        var lag = TimeSpan.FromMinutes(incremental?.LagMinutes ?? DimensionIncremental.DefaultLagMinutes);
        var fullTo = started - lag;
        LoadPlan Full(string? why) => new(DimensionRunModes.Full, null, fullTo, fullTo, why);
        if (incremental is null)
        {
            return Full(null);
        }

        if (request.FullLoad)
        {
            return Full("the run asked for a full load (fullLoad)");
        }

        if (incremental.FullLoad)
        {
            return Full("the flow's incremental block says fullLoad: true");
        }

        var existing = await ledger.FindDimensionAsync(_flow.LedgerId, dimension.Name, ct).ConfigureAwait(false);
        if (existing?.LastRunId is null)
        {
            return Full("it has not been loaded in this partition yet");
        }

        if (existing.LastFullRunId is not { } fullRunId || existing.LastFullBuiltUtc is not { } fullAt)
        {
            return Full("no full load of it has kept the records its keys were read through yet");
        }

        var fullRuns = await ledger.GetDimensionRunsAsync([fullRunId], ct).ConfigureAwait(false);
        if (fullRuns.Count == 0)
        {
            return Full("its last full load is no longer in the ledger");
        }

        var lastFull = fullRuns[0];
        if (!string.Equals(lastFull.Read.RecordKey, incremental.RecordKey, StringComparison.Ordinal))
        {
            return Full(lastFull.Read.RecordKey is null
                ? "its last full load kept no record of the keys each record holds"
                : $"its last full load kept its records by {lastFull.Read.RecordKey}, and the flow's incremental.keyColumns name {incremental.RecordKey}");
        }

        if (!string.Equals(lastFull.DefinitionHash, dimension.DefinitionHash, StringComparison.Ordinal))
        {
            return Full("its declaration changed since its last full load");
        }

        if (!string.Equals(lastFull.Query, query, StringComparison.Ordinal))
        {
            return Full("its query reads other records than its last full load did");
        }

        if (incremental.FullLoadAfterHours is { } hours && started - fullAt > TimeSpan.FromHours(hours))
        {
            return Full(string.Create(CultureInfo.InvariantCulture,
                $"its last full load completed {(started - fullAt).TotalHours:0} hour(s) ago, more than the {hours} incremental.fullLoadAfterHours allows"));
        }

        var from = request.From;
        if (from is null)
        {
            if (await ledger.DimensionReadUpToAsync(existing.DimensionId, ct).ConfigureAwait(false) is not { } upTo)
            {
                return Full("no completed load of it records up to when it read");
            }

            // A window begins a lag before the last one ended: a record the indexer took up to twice the lag to take up is
            // still read, and reading a key again changes nothing.
            from = upTo - lag;
        }

        // A window never ends past the build's start less the lag, so the next one begins no later than what this one read.
        var to = request.To is { } asked && asked < fullTo ? asked : fullTo;
        return new LoadPlan(DimensionRunModes.Incremental, from, to, fullTo, null) { Existing = existing, LastFull = lastFull };
    }

    /// <summary>
    /// The fields the flow's <c>incremental</c> block reads a record's unique key from (each a value a record holds once) and
    /// when it last changed (each a date a record holds once), settled as the dimension's own field is.
    /// </summary>
    /// <exception cref="DeliveryException">A column is not one a record holds once, or a date column holds no date.</exception>
    private async Task<(IReadOnlyList<OsduField> Keys, IReadOnlyList<string> Dates)> SettleRecordColumnsAsync(
        OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, ResolvedField resolved, DimensionIncremental incremental, CancellationToken ct)
    {
        async Task<OsduField> SettleAsync(string column, string at)
        {
            var who = $"{at} of dimension flow '{_flow.Name}', for dimension {dimension.Name},";
            var settled = await ResolvePathAsync(search, templates, dimension, column, who, resolved.Kinds, query, ct).ConfigureAwait(false);
            var field = settled.Field ?? throw new DeliveryException($"{who} reads {column}, and {settled.Note ?? "its field could not be settled."}");
            return field.NestedPath is not null || settled.Repeats
                ? throw new DeliveryException($"{who} names {column}, which a record of {dimension.Kind} can hold more than once, so it cannot stand for one record. Name a property each record holds once, such as id, modifyTime or createTime.")
                : field;
        }

        var keys = new List<OsduField>(incremental.KeyColumns.Count);
        foreach (var column in incremental.KeyColumns)
        {
            keys.Add(await SettleAsync(column, "incremental.keyColumns").ConfigureAwait(false));
        }

        foreach (var column in incremental.DateColumns)
        {
            var field = await SettleAsync(column, "incremental.dateColumns").ConfigureAwait(false);
            if (field.Index != OsduFieldIndex.Date)
            {
                throw new DeliveryException(
                    $"incremental.dateColumns of dimension flow '{_flow.Name}' names {column}, which the index holds as {Index(field.Index)}, not a date, so no window can be read on it. Name dates, such as modifyTime and createTime.");
            }
        }

        return (keys, incremental.DateColumns);
    }

    /// <summary>
    /// Loads a dimension incrementally (see the class's summary), or says why the build loads it in full instead: its field,
    /// or a collected attribute's, is stored otherwise than when it was last loaded; no record matches its query any more;
    /// more than a quarter of its records or keys changed; or more records it was read through changed than are worth
    /// looking up.
    /// </summary>
    private async Task<IncrementalOutcome> LoadIncrementallyAsync(
        ILedger ledger, OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, ResolvedField resolved, LoadPlan load,
        DimensionRunState run, ReadProgress progress, CancellationToken ct)
    {
        var existing = load.Existing ?? throw new InvalidOperationException("An incremental load is planned with the dimension it reads on from.");
        var incremental = _flow.Incremental ?? throw new InvalidOperationException("An incremental load is planned for a flow that loads incrementally.");
        if (resolved.Field is not { } field)
        {
            return IncrementalOutcome.InFull("no record of its kind matches its query now");
        }

        var fieldState = FieldState(field, resolved.Repeats);
        if (existing.Field != fieldState)
        {
            return IncrementalOutcome.InFull($"the index stores {dimension.Path} as {fieldState.AggregateBy} now, and as {existing.Field?.AggregateBy ?? "nothing"} when it was last loaded");
        }

        var collectedStates = (await SettleCollectedAsync(search, templates, dimension, query, resolved, ct).ConfigureAwait(false)).Select(c => c.State).ToList();
        if (!CollectedOf(existing.CollectedJson).SequenceEqual(collectedStates))
        {
            return IncrementalOutcome.InFull("a collected attribute is read otherwise than when it was last loaded");
        }

        var (keyColumns, dateColumns) = await SettleRecordColumnsAsync(search, templates, dimension, query, resolved, incremental, ct).ConfigureAwait(false);
        var templatesJson = resolved.Kinds.Count == 0 ? null : JsonSerializer.Serialize(resolved.Kinds, StepJson);
        var concurrency = Math.Max(1, _flow.Parallelism.RecordsAtOnce);
        var shown = string.Create(CultureInfo.InvariantCulture, $"[{OsduSearch.LuceneTime(load.From)}, {OsduSearch.LuceneTime(load.To)})");

        // Records the last full load read that left the index cannot be seen by any window; a flow that asks loads in full
        // when there are any, before a window of no change would end the build with nothing written.
        (int Searches, long Left)? recordsLeft = null;
        if (incremental.FullLoadWhenRecordsLeave)
        {
            recordsLeft = await RecordsLeftAsync(search, dimension, query, load, ct).ConfigureAwait(false);
            if (recordsLeft.Value.Left > 0)
            {
                return IncrementalOutcome.InFull(string.Create(CultureInfo.InvariantCulture,
                    $"at least {recordsLeft.Value.Left} record(s) its last full load read are gone from the index or no longer match its query, and incremental.fullLoadWhenRecordsLeave asks for a full load then"));
            }
        }
        if (load.From is { } start && start >= load.To)
        {
            var empty = load.Stamp(
                new DimensionReadCounts { Templates = templatesJson, Notes = [$"Loaded incrementally: the window {shown} is empty, so nothing was read."] }, 0, 0);
            return await NothingToWriteAsync(ledger, dimension, run, empty with { RecordKey = incremental.RecordKey }, field.AggregateBy, ct).ConfigureAwait(false);
        }

        // The dimension's own records that changed in the window, each by the time it last changed: counted first, so a
        // window holding most of them loads in full without reading them twice.
        var changedQuery = OsduSearch.Within(query, RecordChanges.Within(dateColumns, load.From, load.To));
        _log.LogInformation("dimension {Dimension}: loading incrementally, reading what changed in {Window}", dimension.Name, shown);
        var changedCount = await search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = changedQuery, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
        var lookups = 1;
        if (load.LastFull?.Read.Records is { } fullRecords && changedCount > DimensionFilters.MaxOriginalsPerQuery && changedCount * FullLoadShare > fullRecords)
        {
            return IncrementalOutcome.InFull(string.Create(CultureInfo.InvariantCulture,
                $"{changedCount} of the {fullRecords} record(s) its last full load read changed in the window, more than one in {FullLoadShare}, so reading every key costs less"));
        }

        if (changedCount > MaxIncrementalRecords)
        {
            return IncrementalOutcome.InFull(string.Create(CultureInfo.InvariantCulture,
                $"{changedCount} record(s) changed in the window, more than the {MaxIncrementalRecords} an incremental load reads, which a full load reads a range at a time"));
        }

        // Each record that changed, by its unique key, with the keys it holds now; and the keys those records held when a
        // load last read them.
        var records = changedCount == 0
            ? new RecordRead([], [], 0, 0, 0, 0)
            : await new DimensionRecordReader(search, _log, concurrency).ReadAllAsync(dimension, changedQuery, field, keyColumns, ct).ConfigureAwait(false);
        var holding = records.Held.Select(h => h.Original).Distinct(StringComparer.Ordinal).ToList();
        var held = await ledger.DimensionRecordKeysAsync(existing.DimensionId, records.Records, ct).ConfigureAwait(false);
        progress.Read = load.Stamp(new DimensionReadCounts { Records = records.Read, ScanPages = records.Pages, CountQueries = lookups, Templates = templatesJson }, records.Read)
            with { RecordKey = incremental.RecordKey };

        // The keys whose label or attributes were read through a record that changed in the window: the records of each
        // entity type the keys were read through, found by when they last changed (OSDU's own times, which every record
        // holds), then the keys by the records.
        var through = new HashSet<string>(StringComparer.Ordinal);
        if (dimension.Label.Count > 0 || dimension.Attributes.Any(a => !a.IsCollected))
        {
            var referenceWindow = RecordChanges.Within(RecordChanges.ModifyTime, load.From, load.To);
            foreach (var type in await ledger.DimensionKeyRecordTypesAsync(existing.DimensionId, ct).ConfigureAwait(false))
            {
                var changedOfType = new OsduSearchQuery { Kind = dimension.LookupKind(type), Query = referenceWindow, ReturnedFields = ["id"] };
                var total = await search.CountAsync(changedOfType, ct).ConfigureAwait(false);
                lookups++;
                if (total == 0)
                {
                    continue;
                }

                if (total > MaxChangedReferences)
                {
                    return IncrementalOutcome.InFull(string.Create(CultureInfo.InvariantCulture,
                        $"{total} {type} record(s) its keys were read through changed in the window, more than the {MaxChangedReferences} an incremental load looks up"));
                }

                var ids = new List<string>();
                await foreach (var page in search.PagesAsync(changedOfType, OsduSearch.MaxPage, ct).ConfigureAwait(false))
                {
                    lookups++;
                    foreach (var hit in page.Hits)
                    {
                        if (hit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } text)
                        {
                            ids.Add(text);
                        }
                    }
                }

                through.UnionWith(await ledger.DimensionKeysReadThroughAsync(existing.DimensionId, ids, ct).ConfigureAwait(false));
            }
        }

        var touched = new HashSet<string>(holding.Where(k => k.Length <= DimensionSpec.MaxOriginalLength), StringComparer.Ordinal);
        touched.UnionWith(held);
        var ofRecords = touched.Count;
        touched.UnionWith(through);
        var found = string.Create(CultureInfo.InvariantCulture,
            $"Loaded incrementally: {records.Read} record(s) changed in {shown}; {touched.Count} key(s) to read again, {ofRecords} that the changed records hold now or held before and {touched.Count - ofRecords} only for a record their label or attributes were read through.");
        if (touched.Count > DimensionFilters.MaxOriginalsPerQuery && (long)touched.Count * FullLoadShare > existing.Originals)
        {
            return IncrementalOutcome.InFull(string.Create(CultureInfo.InvariantCulture,
                $"{touched.Count} of its {existing.Originals} key(s) changed, more than one in {FullLoadShare}, so reading every key costs less"));
        }

        if (touched.Count > MaxIncrementalKeys)
        {
            return IncrementalOutcome.InFull(string.Create(CultureInfo.InvariantCulture,
                $"{touched.Count} of its key(s) are to be read again, more than the {MaxIncrementalKeys} an incremental load reads again, which a full load reads a range at a time"));
        }

        var loadNotes = new List<string> { found };
        if (records.Unidentified > 0)
        {
            loadNotes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{records.Unidentified} changed record(s) hold no single value at incremental.keyColumns ({incremental.RecordKey}), so the keys they held before are not read again; the next full load reads them."));
        }

        var (leftSearches, left) = recordsLeft ?? await RecordsLeftAsync(search, dimension, query, load, ct).ConfigureAwait(false);
        lookups += leftSearches;
        if (left > 0 && load.LastFull is { CompletedUtc: { } fullAt })
        {
            loadNotes.Add(string.Create(CultureInfo.InvariantCulture,
                $"At least {left} record(s) the last full load read ({OsduSearch.LuceneTime(fullAt)}) are gone from the index or no longer match the query; the keys only they held, and the counts they made, stay until the next full load."));
        }
        if (touched.Count == 0)
        {
            var nothing = load.Stamp(new DimensionReadCounts
            {
                Records = records.Read,
                ScanPages = records.Pages,
                CountQueries = lookups,
                Templates = templatesJson,
                Notes = loadNotes,
            }, records.Read, 0) with { RecordKey = incremental.RecordKey };
            return await NothingToWriteAsync(ledger, dimension, run, nothing, field.AggregateBy, ct).ConfigureAwait(false);
        }

        // Every key to read again, counted over the dimension's whole query: a query's worth of keys at a time, each group's
        // records found by its filter, and only the group's own keys kept from what their records hold. Each group's keys are
        // staged as they are counted, in the order the count found them, and are a scope of their own.
        var filterable = touched.Where(k => DimensionFilters.Filterable(field, k)).Order(StringComparer.Ordinal).ToList();
        var unfilterable = touched.Where(k => !DimensionFilters.Filterable(field, k)).Order(StringComparer.Ordinal).ToList();
        var known = (await ledger.DimensionOriginalsAsync(existing.DimensionId, unfilterable, ct).ConfigureAwait(false)).ToDictionary(k => k.Original, StringComparer.Ordinal);
        await using var stage = await ledger.StageDimensionAsync(_flow.LedgerId, run.DimensionId, run.DimensionRunId, ct).ConfigureAwait(false);
        try
        {
            var counted = new HashSet<string>(StringComparer.Ordinal);
            var scopes = new List<DimensionScope>();
            var (aggregations, slices, splits, scannedSlices, scanPages, scannedUnits) = (0, 0, 0, 0, records.Pages, 0L);
            foreach (var group in filterable.Chunk(DimensionFilters.MaxOriginalsPerQuery))
            {
                var scopeQuery = OsduSearch.Within(query, DimensionFilters.Of(field, group)[0]);
                var wanted = new WantedKeys(group.ToHashSet(StringComparer.Ordinal));
                var recount = await DistinctValues.ReadAsync(
                    new SearchDistinctSource(search, dimension.Kind, scopeQuery, field),
                    new DistinctReadOptions(_flow.Source.AggregationSize, dimension.MaxValues, resolved.Repeats, Checks: false, Concurrency: concurrency),
                    _log, wanted, ct).ConfigureAwait(false);
                (aggregations, slices, splits) = (aggregations + recount.Aggregations, slices + recount.Slices, splits + recount.Splits);
                (scannedSlices, scanPages, scannedUnits) = (scannedSlices + recount.ScannedSlices, scanPages + recount.ScanPages, scannedUnits + recount.ScannedUnits);
                if (wanted.Found.Count > 0)
                {
                    await stage.AddKeysAsync(wanted.Found, ct).ConfigureAwait(false);
                    counted.UnionWith(wanted.Found.Keys);
                    scopes.Add(DimensionScope.Of(scopeQuery, wanted.Found, field, concurrency));
                }
            }

            // A key read again and held by no record now is removed, as a full load removes a key it no longer finds.
            var gone = filterable.Where(k => !counted.Contains(k)).ToList();
            await stage.AddGoneAsync(gone, ct).ConfigureAwait(false);

            // A key no query can carry cannot be counted alone: one the dimension holds stays as it is, and a new one is kept
            // with the changed records holding it, its collected values and elements left to the next full load.
            var kept = unfilterable.Where(k => known.TryGetValue(k, out var state) && state.RemovedRunId is null).ToList();
            var holders = records.Held.GroupBy(h => h.Original, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
            var added = unfilterable.Except(kept, StringComparer.Ordinal).Select(k => new KeyValuePair<string, long>(k, holders.GetValueOrDefault(k))).ToList();
            await stage.AddKeysAsync(added, ct).ConfigureAwait(false);
            if (gone.Count > 0)
            {
                loadNotes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{gone.Count} key(s) read again are held by no record now and are removed: {Examples(gone)}."));
            }

            if (kept.Count > 0)
            {
                loadNotes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{kept.Count} key(s) to read again cannot be carried in a query, so they stay as they were until a full load reads them: {Examples(kept)}."));
            }

            var countQueries = lookups;
            var merged = new DistinctCounts
            {
                Keys = counted.Count + added.Count,
                Records = records.Read,
                Aggregations = aggregations,
                Slices = slices,
                Splits = splits,
                ScannedSlices = scannedSlices,
                ScanPages = scanPages,
                ScannedUnits = scannedUnits,
                Notes = loadNotes,
            };
            progress.Read = load.Stamp(Counts(merged, templatesJson, countQueries, merged.Notes, LabelCounts.None), records.Read, touched.Count) with { RecordKey = incremental.RecordKey };
            var read = await ReadKeysAsync(
                search, templates, dimension, stage, scopes, query, field, resolved, 0, progress,
                soFar => load.Stamp(Counts(merged, templatesJson, countQueries, merged.Notes, soFar), records.Read, touched.Count) with { RecordKey = incremental.RecordKey },
                ct).ConfigureAwait(false);

            // The values the keys read again were and are under, each laid out from all its keys: those read again, and the
            // others as the dimension holds them; a key removed is under none.
            var notes = new List<string>(loadNotes);
            notes.AddRange(read.Notes);
            var (memberNotes, memberCounts) = await MembersAsync(search, dimension, query, field, resolved.Repeats, stage, partial: true, ct).ConfigureAwait(false);
            notes.AddRange(memberNotes);
            notes.AddRange(read.Collected.Notes);
            notes.AddRange(read.ElementNotes);
            await stage.AddRecordsAsync(records.Records, records.Held, ct).ConfigureAwait(false);
            var counts = Counts(merged, templatesJson, countQueries + memberCounts, notes, read.Labels, read.Collected);
            progress.Read = load.Stamp(counts with { ScanPages = counts.ScanPages + read.ElementPages }, records.Read, touched.Count) with { RecordKey = incremental.RecordKey };
            var result = await ledger.WriteDimensionAsync(
                stage, Staged(dimension, fieldState, progress.Read, read.Collected, partial: true, keepsRecords: true), ct).ConfigureAwait(false);
            _log.LogInformation(
                "dimension {Dimension}: loaded incrementally, {Read} key(s) read again and {Gone} removed; {Values} value(s) from {Keys} key(s); {Added} arrived, {Moved} moved, {Restored} came back",
                dimension.Name, stage.Values, result.Changes.OriginalsRemoved, result.Members, result.Originals, result.Changes.OriginalsAdded, result.Changes.OriginalsMoved,
                result.Changes.OriginalsRestored);
            return new IncrementalOutcome(Summary(dimension, result, progress.Read, field.AggregateBy), null);
        }
        finally
        {
            await DiscardAsync(stage, dimension).ConfigureAwait(false);
        }
    }

    /// <summary>Closes an incremental load that found nothing to write as completed, the dimension as it was.</summary>
    private async Task<IncrementalOutcome> NothingToWriteAsync(
        ILedger ledger, DimensionSpec dimension, DimensionRunState run, DimensionReadCounts read, string? aggregateBy, CancellationToken ct)
    {
        await ledger.CompleteDimensionRunAsync(run.DimensionRunId, read, Now, ct).ConfigureAwait(false);
        var runs = await ledger.GetDimensionRunsAsync([run.DimensionRunId], ct).ConfigureAwait(false);
        var closed = (runs.Count > 0 ? runs[0] : null)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {run.DimensionRunId} is not in the ledger after it completed."));
        _log.LogInformation("dimension {Dimension}: loaded incrementally, nothing to write: {Note}", dimension.Name, read.Notes.Count > 0 ? read.Notes[0] : string.Empty);
        return new IncrementalOutcome(Summary(dimension, closed, read, aggregateBy), null);
    }

    /// <summary>
    /// How many records the last full load read are no longer in the index or no longer match the dimension's query, when it
    /// can tell, which no incremental load can see: those it read, less those the query matches now that were created before
    /// it completed. A record created while it read, and one the indexer took up late, make the count lower, never higher, so
    /// it is a least; none when the last full load kept no count. Returns the searches it asked, and the count.
    /// </summary>
    private static async Task<(int Searches, long Left)> RecordsLeftAsync(OsduSearch search, DimensionSpec dimension, string? query, LoadPlan load, CancellationToken ct)
    {
        if (load.LastFull is not { Read.Records: { } fullRecords, CompletedUtc: { } fullAt })
        {
            return (0, 0);
        }

        var now = await search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = query, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
        var since = OsduSearch.Within(query, $"{RecordChanges.CreateTime}:[{OsduSearch.LuceneTime(fullAt)} TO *]");
        var created = await search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = since, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
        return (2, Math.Max(0, fullRecords - (now - created)));
    }

    /// <summary>The first few keys of a list, quoted, for a note.</summary>
    private static string Examples(IReadOnlyList<string> keys)
        => string.Join(", ", keys.Take(5).Select(k => $"'{Shown(k)}'")) + (keys.Count > 5 ? ", ..." : string.Empty);

    /// <summary>
    /// The keys of a group a recount reads again, among every key the group's records hold: each kept with how many records
    /// hold it, in the order the recount found it, and every other let go.
    /// </summary>
    private sealed class WantedKeys(HashSet<string> wanted) : IDistinctSink
    {
        private readonly Dictionary<string, long> _found = new(StringComparer.Ordinal);

        /// <summary>The group's keys some record holds, with how many hold each.</summary>
        public IReadOnlyDictionary<string, long> Found => _found;

        public ValueTask AddAsync(IReadOnlyList<KeyValuePair<string, long>> values, CancellationToken ct)
        {
            foreach (var (key, count) in values)
            {
                if (wanted.Contains(key))
                {
                    _found[key] = _found.GetValueOrDefault(key) + count;
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(CancellationToken ct)
        {
            _found.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
