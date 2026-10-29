using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>What a pass learned of one kind's schema: the kind as the service lists it, and every place its records name another record.</summary>
/// <param name="Listing">The kind as the service lists it.</param>
/// <param name="Relationships">The places its records name another record, in the order its schema declares them.</param>
/// <param name="Unresolved">The references its bundle left as they were (a schema the service does not hold, a form the reader does not follow), behind which a place is not seen.</param>
/// <param name="Cut">Whether the walk of its schema stopped at its bound.</param>
public sealed record SchemaKindFacts(SchemaListing Listing, IReadOnlyList<SchemaRelationship> Relationships, IReadOnlyList<string> Unresolved, bool Cut)
{
    /// <summary>Whether a place of its records may not be seen: a reference its bundle did not follow, or a walk cut short.</summary>
    public bool Partial => Unresolved.Count > 0 || Cut;
}

/// <summary>A kind the service lists whose schema a pass could not read, and why.</summary>
public sealed record SchemaKindUnread(string Kind, string Why);

/// <summary>One complete reading of a partition's schemas.</summary>
/// <param name="ReadUtc">When the pass that made it ended.</param>
/// <param name="Listed">How many schemas the service listed, of every status and scope: the kinds and the schemas they are made of.</param>
/// <param name="Kinds">Each kind read, by its id.</param>
/// <param name="Unread">The kinds listed whose schema could not be read, each with why; at most <see cref="PartitionSchemaIndex.MaxUnreadKept"/>.</param>
/// <param name="UnreadCount">How many kinds could not be read in all.</param>
/// <param name="Notes">What leaves the reading short other than a kind not read: a listing the service refused, entries that name no schema.</param>
public sealed record SchemaIndexReading(
    DateTimeOffset ReadUtc, int Listed, IReadOnlyDictionary<string, SchemaKindFacts> Kinds, IReadOnlyList<SchemaKindUnread> Unread, int UnreadCount, IReadOnlyList<string> Notes);

/// <summary>Where a pass under way stands.</summary>
/// <param name="StartedUtc">When it started.</param>
/// <param name="Listing">True while it lists the schemas, before it reads any.</param>
/// <param name="Listed">How many schemas it listed.</param>
/// <param name="ToRead">How many kinds it reads: those not read before, and those in development, which may have changed.</param>
/// <param name="Read">How many of them it has read.</param>
/// <param name="Failed">How many of them it could not read.</param>
public sealed record SchemaIndexProgress(DateTimeOffset StartedUtc, bool Listing, int Listed, int ToRead, int Read, int Failed);

/// <summary>What an ask of the index sees: the last complete reading, the pass under way, and why the last pass failed when it did.</summary>
public sealed record SchemaIndexView(SchemaIndexReading? Last, SchemaIndexProgress? Progress, string? Problem, DateTimeOffset? ProblemUtc);

/// <summary>The connection a pass reads through, and the hold on it the pass lets go when it ends.</summary>
public sealed class SchemaIndexConnection : IDisposable
{
    private readonly IDisposable _hold;

    public SchemaIndexConnection(OsduHttpClient client, IDisposable hold)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(hold);
        Client = client;
        _hold = hold;
    }

    public OsduHttpClient Client { get; }

    public void Dispose() => _hold.Dispose();
}

/// <summary>
/// What a partition's Schema service holds, read once and kept (osdu/docs/reference/concepts/explorer.md, Referenced
/// by): every kind it lists, each with the places its records name another record (<see cref="SchemaRelationships"/>),
/// so the kinds that refer to a type are answered from what was read rather than by reading every schema again. The
/// service has no way to ask which schemas refer to a type: it lists schemas without their content, so the only answer
/// is every schema read.
/// </summary>
/// <remarks>
/// <para>
/// A pass lists the schemas of every status and scope (<see cref="SchemaServiceReader.ListAsync"/>), then reads the schema
/// of each kind, bundled with the schemas it refers to, <see cref="Concurrency"/> at a time; a schema referred to by many
/// kinds is read once in a pass (<see cref="SchemaServiceTexts"/>). An abstract schema is read only as a kind refers to it,
/// since no record is of it. A pass runs on its own, through a connection of its own, for whoever asks next: an ask that
/// gives up waiting leaves it running, and the next ask joins it.
/// </para>
/// <para>
/// A pass after the first reads only what may have changed: the kinds it did not hold before and those in development. A
/// published or obsolete schema never changes, so what was read of it stands; a kind the service no longer lists is
/// dropped. A pass starts when nothing has been read, when the reader asks for one, and when the last reading is older
/// than <see cref="MaxAge"/>; the reading it replaces answers meanwhile. A pass that fails leaves the last reading as it
/// was, and says why; another starts only when asked for, or after <see cref="RetryAfter"/>.
/// </para>
/// <para>
/// A kind whose schema cannot be read is listed with why, and the pass goes on; a pass that cannot read
/// <see cref="MaxFailures"/> kinds, more than it read, stops there, since the service is failing rather than one schema.
/// A listing the service refuses as asked (400, a status or scope it does not know) is noted; any other refusal fails the
/// pass. The log has one line for each pass, never one for each schema.
/// </para>
/// </remarks>
public sealed partial class PartitionSchemaIndex : IDisposable
{
    /// <summary>How many schemas a pass reads at once.</summary>
    public const int Concurrency = 8;

    /// <summary>How many kinds a pass may fail to read, more than it read, before it stops.</summary>
    public const int MaxFailures = 50;

    /// <summary>The most kinds not read a reading lists, with why; the rest are counted.</summary>
    public const int MaxUnreadKept = 100;

    /// <summary>How old a reading grows before an ask starts a pass that brings it up to date.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>How long after a pass failed an ask that does not ask for a pass leaves the failure standing.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    /// <summary>The operation code a pass's requests carry in their correlation id (osdu/docs/reference/concepts/request-correlation.md).</summary>
    public const string CorrelationOperation = "schema";

    private readonly Lock _gate = new();
    private readonly string _label;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private SchemaIndexReading? _last;
    private Pass? _pass;
    private string? _problem;
    private DateTimeOffset? _problemUtc;
    private bool _disposed;

    /// <param name="label">What the log calls the partition: its id, or the endpoint where it has none.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">Where each pass is logged.</param>
    public PartitionSchemaIndex(string label, TimeProvider time, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _label = label;
        _time = time;
        _log = log;
        LastAsked = time.GetUtcNow();
    }

    /// <summary>When the index was last asked.</summary>
    public DateTimeOffset LastAsked { get; private set; }

    /// <summary>Whether a pass is under way.</summary>
    public bool Reading
    {
        get
        {
            lock (_gate)
            {
                return _pass is not null;
            }
        }
    }

    /// <summary>
    /// Starts a pass when one is due (nothing read yet, <paramref name="refresh"/>, or a reading older than
    /// <see cref="MaxAge"/>; after a failed pass only when asked for, or after <see cref="RetryAfter"/>), reading through
    /// the connection <paramref name="open"/> gives it. Answers the pass under way, which never fails; null when none is.
    /// </summary>
    public Task? Ensure(Func<CancellationToken, Task<SchemaIndexConnection>> open, bool refresh)
    {
        ArgumentNullException.ThrowIfNull(open);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            LastAsked = now;
            if (_pass is not null)
            {
                return _pass.Task;
            }

            if (_disposed)
            {
                return null;
            }

            var failedLately = _problemUtc is { } failed && now - failed < RetryAfter && (_last is null || failed > _last.ReadUtc);
            var due = refresh || (!failedLately && (_last is null || now - _last.ReadUtc > MaxAge));
            if (!due)
            {
                return null;
            }

            var pass = new Pass(now);
            var previous = _last;
            var stopping = _stopping.Token;
            _pass = pass;

            // The pass is nobody's request: it carries none of the asker's context, and runs on after the asker is gone.
            if (ExecutionContext.IsFlowSuppressed())
            {
                pass.Task = Task.Run(() => RunAsync(pass, open, previous, stopping), CancellationToken.None);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    pass.Task = Task.Run(() => RunAsync(pass, open, previous, stopping), CancellationToken.None);
                }
            }

            return pass.Task;
        }
    }

    /// <summary>What an ask sees now.</summary>
    public SchemaIndexView View()
    {
        lock (_gate)
        {
            return new SchemaIndexView(_last, _pass?.Progress(), _problem, _problemUtc);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // A pass under way stops at its next read; it lets its connection go as it ends.
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task RunAsync(Pass pass, Func<CancellationToken, Task<SchemaIndexConnection>> open, SchemaIndexReading? previous, CancellationToken ct)
    {
        // A pass outlives the ask that started it and serves every later one, so its requests carry a correlation id of their own.
        using var correlation = OsduCorrelation.Detach(CorrelationOperation);
        try
        {
            var reading = await ReadAsync(pass, open, previous, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _last = reading;
                _problem = null;
                _problemUtc = null;
                _pass = null;
            }

            LogRead(_log, _label, reading.Kinds.Count, pass.ToRead, reading.UnreadCount, reading.Listed, correlation.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _pass = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A pass answers whoever asks next, so its failure is kept for them to read, and logged whole here.
            var why = HeaderRedaction.RedactMessage(ex.Message);
            lock (_gate)
            {
                _problem = why;
                _problemUtc = _time.GetUtcNow();
                _pass = null;
            }

            LogFailed(_log, ex, _label, why, correlation.Id);
        }
    }

    private async Task<SchemaIndexReading> ReadAsync(Pass pass, Func<CancellationToken, Task<SchemaIndexConnection>> open, SchemaIndexReading? previous, CancellationToken ct)
    {
        using var connection = await open(ct).ConfigureAwait(false);
        var reader = new SchemaServiceReader(connection.Client, _time, texts: new SchemaServiceTexts());

        // Every status and scope, each listed apart: the service lists one of each at a time, published and internal when
        // a listing names neither.
        var listed = new Dictionary<string, SchemaListing>(StringComparer.Ordinal);
        var notes = new List<string>();
        var malformed = 0;
        foreach (var status in SchemaServiceReader.Statuses)
        {
            foreach (var scope in SchemaServiceReader.Scopes)
            {
                SchemaServiceListing listing;
                try
                {
                    listing = await reader.ListAsync(status, scope, ct).ConfigureAwait(false);
                }
                catch (OsduStatusException ex) when (ex.StatusCode == 400)
                {
                    notes.Add($"The Schema service refused to list its {status.ToLowerInvariant()} {scope.ToLowerInvariant()} schemas: {HeaderRedaction.RedactMessage(ex.Message)}");
                    continue;
                }

                malformed += listing.Malformed;
                foreach (var schema in listing.Schemas)
                {
                    listed.TryAdd(schema.Id, schema);
                }

                pass.Listed = listed.Count;
            }
        }

        if (notes.Count == SchemaServiceReader.Statuses.Count * SchemaServiceReader.Scopes.Count)
        {
            throw new DeliveryException("The Schema service refused every listing of its schemas. " + string.Join(" ", notes));
        }

        if (malformed > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"The Schema service listed {malformed} entries that name no schema id, which were passed over."));
        }

        // What was read of a settled schema stands; a kind in development, one not read before and one read differently
        // since (a status that moved out of development) is read now.
        var kinds = new ConcurrentDictionary<string, SchemaKindFacts>(StringComparer.Ordinal);
        var toRead = new List<SchemaListing>();
        foreach (var listing in listed.Values.Where(l => l.IsKind))
        {
            if (previous is not null && previous.Kinds.TryGetValue(listing.Id, out var known) && listing.Settled && known.Listing.Settled)
            {
                kinds[listing.Id] = known with { Listing = listing };
            }
            else
            {
                toRead.Add(listing);
            }
        }

        pass.ToRead = toRead.Count;
        pass.Listing = false;
        var unread = new ConcurrentQueue<SchemaKindUnread>();
        await Parallel.ForEachAsync(
            toRead,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (listing, token) =>
            {
                var why = await ReadKindAsync(reader, listing, kinds, token).ConfigureAwait(false);
                if (why is null)
                {
                    pass.CountRead();
                    return;
                }

                unread.Enqueue(new SchemaKindUnread(listing.Id, why));
                var (failed, read) = pass.CountFailed();
                if (failed >= MaxFailures && failed > read)
                {
                    throw new DeliveryException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The Schema service failed {failed} reads of a schema, more than it answered ({read}), so the pass stopped. The last: {listing.Id}: {why}"));
                }
            }).ConfigureAwait(false);

        var notRead = unread.OrderBy(u => u.Kind, StringComparer.Ordinal).ToList();
        return new SchemaIndexReading(
            _time.GetUtcNow(),
            listed.Count,
            new Dictionary<string, SchemaKindFacts>(kinds, StringComparer.Ordinal),
            notRead.Take(MaxUnreadKept).ToList(),
            notRead.Count,
            notes);
    }

    /// <summary>Reads one kind into <paramref name="kinds"/>; answers why it could not, or null when it did.</summary>
    private async Task<string?> ReadKindAsync(SchemaServiceReader reader, SchemaListing listing, ConcurrentDictionary<string, SchemaKindFacts> kinds, CancellationToken ct)
    {
        SchemaServiceRead read;
        try
        {
            read = await reader.ReadAsync(listing.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or TimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return HeaderRedaction.RedactMessage(ex.Message);
        }

        if (read.Schema is null)
        {
            return "the Schema service lists it, yet holds no schema under its id";
        }

        try
        {
            var found = SchemaRelationships.Of(SchemaRules.Of(read.Schema));
            kinds[listing.Id] = new SchemaKindFacts(listing, found.Places, read.Unresolved, found.Cut);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A schema of the partition's own may be one the rules cannot be made of; it is that kind's to say, not the pass's.
            LogUnwalked(_log, ex, _label, listing.Id);
            return $"its schema could not be read for the records it names: {ex.Message}";
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Read the schemas of {Partition}: {Kinds} kinds held, {Read} read in this pass, {Unread} not read, of {Listed} schemas listed (correlation {CorrelationId}).")]
    private static partial void LogRead(ILogger logger, string partition, int kinds, int read, int unread, int listed, string correlationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "The schemas of {Partition} could not be read: {Reason} (correlation {CorrelationId}).")]
    private static partial void LogFailed(ILogger logger, Exception exception, string partition, string reason, string correlationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The schema {Kind} of {Partition} could not be read for the records it names.")]
    private static partial void LogUnwalked(ILogger logger, Exception exception, string partition, string kind);

    /// <summary>One pass, and where it stands, counted as its reads end.</summary>
    private sealed class Pass(DateTimeOffset started)
    {
        private int _read;
        private int _failed;
        private volatile int _listed;
        private volatile int _toRead;
        private volatile bool _listing = true;

        public Task Task { get; set; } = Task.CompletedTask;

        public int Listed
        {
            get => _listed;
            set => _listed = value;
        }

        public int ToRead
        {
            get => _toRead;
            set => _toRead = value;
        }

        public bool Listing
        {
            get => _listing;
            set => _listing = value;
        }

        public void CountRead() => Interlocked.Increment(ref _read);

        /// <summary>Counts one more kind not read; answers how many failed and how many were read.</summary>
        public (int Failed, int Read) CountFailed() => (Interlocked.Increment(ref _failed), Volatile.Read(ref _read));

        public SchemaIndexProgress Progress()
            => new(started, Listing, Listed, ToRead, Volatile.Read(ref _read), Volatile.Read(ref _failed));
    }
}

/// <summary>
/// The schema indexes the control plane keeps (<see cref="PartitionSchemaIndex"/>), one for each Schema service and partition
/// read, so every reader of a partition shares one reading. At most <see cref="MaxPartitions"/> are kept; past it the one
/// asked longest ago is let go, never one with a pass under way.
/// </summary>
public sealed class PartitionSchemaIndexes : IDisposable
{
    /// <summary>The most partitions whose schemas are kept at once.</summary>
    public const int MaxPartitions = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PartitionSchemaIndex> _indexes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private bool _disposed;

    public PartitionSchemaIndexes(TimeProvider time, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggers);
        _time = time;
        _loggers = loggers;
    }

    /// <summary>The index of the schemas the service at <paramref name="endpoint"/> holds for <paramref name="partition"/>.</summary>
    public PartitionSchemaIndex For(string endpoint, string? partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var key = endpoint.TrimEnd('/') + "\n" + (partition ?? string.Empty);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_indexes.TryGetValue(key, out var held))
            {
                return held;
            }

            while (_indexes.Count >= MaxPartitions)
            {
                var idle = _indexes.Where(e => !e.Value.Reading).OrderBy(e => e.Value.LastAsked).Select(e => e.Key).FirstOrDefault();
                if (idle is null)
                {
                    break;
                }

                _indexes.Remove(idle, out var dropped);
                dropped?.Dispose();
            }

            var index = new PartitionSchemaIndex(
                string.IsNullOrWhiteSpace(partition) ? endpoint : partition, _time, _loggers.CreateLogger<PartitionSchemaIndex>());
            _indexes[key] = index;
            return index;
        }
    }

    public void Dispose()
    {
        List<PartitionSchemaIndex> held;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            held = [.. _indexes.Values];
            _indexes.Clear();
        }

        foreach (var index in held)
        {
            index.Dispose();
        }
    }
}
