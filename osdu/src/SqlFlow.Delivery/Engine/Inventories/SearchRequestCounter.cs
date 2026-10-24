using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>
/// Counts the requests a match or a duplicate check sends to the search service (its plain search, its cursor search and the
/// release of a cursor) and, for a check (<paramref name="storageReads"/>), storage's read of records by id, each request once
/// however many attempts it took, and hands every attempt and retry on to the run's own observer, so the run can say what it
/// cost and its live trace still shows each call. Token requests and anything else the stack sends are not counted.
/// </summary>
internal sealed class SearchRequestCounter(InventorySource source, IHttpObserver? inner, bool storageReads = false) : IHttpObserver
{
    private readonly string _queryPath = source.QueryPath.TrimEnd('/');
    private readonly string _cursorPath = source.SearchPath.TrimEnd('/');
    private readonly string? _recordsPath = storageReads ? source.RecordQueryPath.TrimEnd('/') : null;
    private long _sent;
    private long _taken;

    /// <summary>Every request counted so far.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>The requests counted since the last take, which a write of the run adds to its count.</summary>
    public long Take()
    {
        var sent = Interlocked.Read(ref _sent);
        var taken = Interlocked.Exchange(ref _taken, sent);
        return Math.Max(0, sent - taken);
    }

    public void Ended(HttpAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt == 1 && Counts(attempt.Url))
        {
            Interlocked.Increment(ref _sent);
        }

        inner?.Ended(attempt);
    }

    public void Retrying(HttpRetry retry) => inner?.Retrying(retry);

    private bool Counts(string url)
    {
        var path = (Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : url).TrimEnd('/');
        return path.EndsWith(_queryPath, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(_cursorPath, StringComparison.OrdinalIgnoreCase)
            || path.Contains(_cursorPath + "/", StringComparison.OrdinalIgnoreCase)
            || (_recordsPath is not null && path.EndsWith(_recordsPath, StringComparison.OrdinalIgnoreCase));
    }
}
