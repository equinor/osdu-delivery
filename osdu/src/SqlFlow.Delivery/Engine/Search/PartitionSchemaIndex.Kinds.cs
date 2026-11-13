using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>One listing of the kinds a partition's Schema service holds, without any schema's content.</summary>
/// <param name="ReadUtc">When the listing ended.</param>
/// <param name="Kinds">
/// Each kind records can be of (<see cref="SchemaListing.IsKind"/>), by its id: every status and scope, the partition's own
/// and the platform's, whether or not any record is of it.
/// </param>
/// <param name="Listed">How many schemas the service listed, the abstract ones included.</param>
/// <param name="Notes">What leaves the listing short: a status and scope the service would not list.</param>
public sealed record SchemaKindsListing(DateTimeOffset ReadUtc, IReadOnlyDictionary<string, SchemaListing> Kinds, int Listed, IReadOnlyList<string> Notes);

/// <summary>What an ask of the kinds sees: the last listing, whether one is under way, and why the last one failed when it did.</summary>
public sealed record SchemaKindsView(SchemaKindsListing? Last, bool Listing, string? Problem, DateTimeOffset? ProblemUtc)
{
    /// <summary>The listing is under way and none was made before it.</summary>
    public const string Reading = "reading";

    /// <summary>A listing answers.</summary>
    public const string Ready = "ready";

    /// <summary>No listing could be made; <see cref="Problem"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Where the listing stands for a reader: <see cref="Reading"/>, <see cref="Ready"/> or <see cref="Failed"/>.</summary>
    public string State => Last is not null ? Ready : Listing ? Reading : Failed;
}

public sealed partial class PartitionSchemaIndex
{
    /// <summary>How old a listing of the kinds grows before an ask lists them again: a schema registered since shows within it.</summary>
    public static readonly TimeSpan KindsMaxAge = TimeSpan.FromMinutes(10);

    private SchemaKindsListing? _kinds;
    private Task? _kindsListing;
    private string? _kindsProblem;
    private DateTimeOffset? _kindsProblemUtc;

    /// <summary>
    /// Starts a listing of the kinds when one is due (none made yet, <paramref name="refresh"/>, or the last older than
    /// <see cref="KindsMaxAge"/>; after a failed one only when asked for, or after <see cref="RetryAfter"/>), reading through
    /// the connection <paramref name="open"/> gives it. Answers the listing under way, which never fails; null when none is.
    /// </summary>
    public Task? EnsureKinds(Func<CancellationToken, Task<SchemaIndexConnection>> open, bool refresh)
    {
        ArgumentNullException.ThrowIfNull(open);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            LastAsked = now;
            if (_kindsListing is not null)
            {
                return _kindsListing;
            }

            if (_disposed)
            {
                return null;
            }

            var failedLately = _kindsProblemUtc is { } failed && now - failed < RetryAfter && (_kinds is null || failed > _kinds.ReadUtc);
            var due = refresh || (!failedLately && (_kinds is null || now - _kinds.ReadUtc > KindsMaxAge));
            if (!due)
            {
                return null;
            }

            // The listing is nobody's request: it carries none of the asker's context, and runs on after the asker is gone.
            var stopping = _stopping.Token;
            if (ExecutionContext.IsFlowSuppressed())
            {
                _kindsListing = Task.Run(() => ListKindsAsync(open, stopping), CancellationToken.None);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    _kindsListing = Task.Run(() => ListKindsAsync(open, stopping), CancellationToken.None);
                }
            }

            return _kindsListing;
        }
    }

    /// <summary>What an ask of the kinds sees now.</summary>
    public SchemaKindsView KindsView()
    {
        lock (_gate)
        {
            return new SchemaKindsView(_kinds, _kindsListing is not null, _kindsProblem, _kindsProblemUtc);
        }
    }

    private async Task ListKindsAsync(Func<CancellationToken, Task<SchemaIndexConnection>> open, CancellationToken ct)
    {
        using var correlation = OsduCorrelation.Detach(CorrelationOperation);
        try
        {
            var listing = await ListKindsOnceAsync(open, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _kinds = listing;
                _kindsProblem = null;
                _kindsProblemUtc = null;
                _kindsListing = null;
            }

            LogKinds(_log, _label, listing.Kinds.Count, listing.Listed, correlation.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _kindsListing = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = HeaderRedaction.RedactMessage(ex.Message);
            lock (_gate)
            {
                _kindsProblem = why;
                _kindsProblemUtc = _time.GetUtcNow();
                _kindsListing = null;
            }

            LogKindsFailed(_log, ex, _label, why, correlation.Id);
        }
    }

    /// <summary>
    /// Every status and scope listed at once, each a listing of its own, since the service lists one of each at a time. A
    /// listing the service refuses as asked (400) is noted; any other refusal, or every listing refused, fails it.
    /// </summary>
    private async Task<SchemaKindsListing> ListKindsOnceAsync(Func<CancellationToken, Task<SchemaIndexConnection>> open, CancellationToken ct)
    {
        using var connection = await open(ct).ConfigureAwait(false);
        var reader = new SchemaServiceReader(connection.Client, _time);
        var asks = SchemaServiceReader.Statuses.SelectMany(status => SchemaServiceReader.Scopes.Select(scope => (Status: status, Scope: scope))).ToList();
        var answers = await Task.WhenAll(asks.Select(async ask =>
        {
            try
            {
                return (ask.Status, ask.Scope, Listing: await reader.ListAsync(ask.Status, ask.Scope, ct).ConfigureAwait(false), Refused: (string?)null);
            }
            catch (OsduStatusException ex) when (ex.StatusCode == 400)
            {
                return (ask.Status, ask.Scope, Listing: (SchemaServiceListing?)null, Refused: HeaderRedaction.RedactMessage(ex.Message));
            }
        })).ConfigureAwait(false);

        var notes = answers
            .Where(a => a.Refused is not null)
            .Select(a => $"The Schema service refused to list its {a.Status.ToLowerInvariant()} {a.Scope.ToLowerInvariant()} schemas: {a.Refused}")
            .ToList();
        if (notes.Count == answers.Length)
        {
            throw new DeliveryException("The Schema service refused every listing of its schemas. " + string.Join(" ", notes));
        }

        // A schema is listed in one status and scope; were the service to list one twice, the first status asked (published) holds.
        var listed = new Dictionary<string, SchemaListing>(StringComparer.Ordinal);
        foreach (var schema in answers.Where(a => a.Listing is not null).SelectMany(a => a.Listing!.Schemas))
        {
            listed.TryAdd(schema.Id, schema);
        }

        var malformed = answers.Sum(a => a.Listing?.Malformed ?? 0);
        if (malformed > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"The Schema service listed {malformed} entries that name no schema id, which were passed over."));
        }

        var kinds = listed.Values.Where(l => l.IsKind).ToDictionary(l => l.Id, StringComparer.Ordinal);
        return new SchemaKindsListing(_time.GetUtcNow(), kinds, listed.Count, notes);
    }

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Listed the kinds of {Partition}: {Kinds} kinds of {Listed} schemas listed (correlation {CorrelationId}).")]
    private static partial void LogKinds(ILogger logger, string partition, int kinds, int listed, string correlationId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "The kinds of {Partition} could not be listed: {Reason} (correlation {CorrelationId}).")]
    private static partial void LogKindsFailed(ILogger logger, Exception exception, string partition, string reason, string correlationId);
}
