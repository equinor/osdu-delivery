using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What a run knows about records it must find on the platform, and how it learns the rest: the records a mapping searches
/// for, and the parent a record of a linked template reads sections of (osdu/docs/reference/flow/mapping-parent.md).
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of resolving a relationship, beside the partition's cache. A cache holds a closed set that a
/// capture can hold whole: the units, the type codes, the vocabularies a partition defines once and changes rarely. The
/// records a delivery refers to are not that. A partition's wellbores are business data: they grow without bound, they
/// change constantly, and capturing them to answer one lookup costs more every day it runs. Those are searched for, one
/// value at a time, with a query that can match only the record that holds exactly that value.
/// </para>
/// <para>
/// A render asks its questions while it runs, not before, so a mapping may search on a value the render itself
/// produced: a column after its modifiers, anything the row makes available. Resolving before the render would allow
/// only what is known before it starts.
/// </para>
/// <para>
/// The render itself stays synchronous and free of I/O. It asks <see cref="TryAnswer"/> for what the run already knows;
/// a question nobody has asked yet leaves the render incomplete, naming what it needs. The caller answers those with
/// <see cref="AnswerAsync"/>, a batch of records' questions in one round, and renders again. Answers are kept for the
/// run, so only a row that asks something new pays for another pass, and the thousandth record referring to one
/// wellbore asks nothing.
/// </para>
/// <para>
/// An implementation is shared by the plan's parallel renderers: both members are safe to call concurrently, and a
/// question asked by two of them at once is sent once.
/// </para>
/// </remarks>
public interface IRecordSearch
{
    /// <summary>
    /// The answer this run already has for <paramref name="question"/>, or false when it has never been asked. Finding
    /// nothing is an answer too, and is not asked again.
    /// </summary>
    bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer);

    /// <summary>
    /// The record's parent as this run read it, or false when it has not been read yet. A parent the platform does not hold
    /// is an answer too, and is not read again.
    /// </summary>
    bool TryRead(ParentQuestion question, [NotNullWhen(true)] out ParentAnswer? answer);

    /// <summary>
    /// Answers every question in <paramref name="questions"/> this run has not answered yet, searches and parents alike, so
    /// the renders that asked them can run again and finish. Asking for none does nothing.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// The platform could not be asked: it was unreachable, refused the run's credentials, or failed after its retries.
    /// An answer that did not come is never taken for finding nothing, which would hold records for the wrong reason.
    /// </exception>
    Task AnswerAsync(IReadOnlyCollection<PlatformQuestion> questions, CancellationToken ct = default);
}

/// <summary>
/// What reading a record's parent came to (osdu/docs/reference/flow/mapping-parent.md): the parent the platform holds, with
/// the sections the mapping reads of it, or that it holds none the flow may read.
/// </summary>
public sealed record ParentAnswer
{
    private ParentAnswer(JsonObject? record, string? kind, long? version)
    {
        Record = record;
        Kind = kind;
        Version = version;
    }

    /// <summary>
    /// The sections the mapping reads, laid out at their paths from the record's root, as the platform holds them; null when
    /// the platform holds no parent. Shared by every render that reads the parent, so a section is copied before it is written.
    /// </summary>
    public JsonObject? Record { get; }

    /// <summary>The kind the parent the platform holds is of; null when it holds none.</summary>
    public string? Kind { get; }

    /// <summary>The version of the parent read; null when the platform holds none.</summary>
    public long? Version { get; }

    public bool Found => Record is not null;

    /// <summary>The platform holds no record under the parent's id the flow may read: none was delivered, it was deleted, or the flow's identity may not view it.</summary>
    public static ParentAnswer None { get; } = new(null, null, null);

    /// <summary>The parent the platform holds, of kind <paramref name="kind"/> at version <paramref name="version"/>, with the sections <paramref name="sections"/> lays out.</summary>
    public static ParentAnswer Of(JsonObject sections, string kind, long version)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return new ParentAnswer(sections, kind, version);
    }

    /// <summary>The section at <paramref name="path"/>, or null when the parent holds none there (or the platform holds no parent).</summary>
    public JsonNode? At(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonNode? node = Record;
        foreach (var step in path.Split('.'))
        {
            if (node is not JsonObject holder || !holder.TryGetPropertyValue(step, out node))
            {
                return null;
            }
        }

        return node;
    }
}

/// <summary>
/// The parent a render read sections of, kept so a record says which parent, and which version of it, its document was
/// built from (osdu/docs/reference/flow/mapping-parent.md, What the ledger keeps).
/// </summary>
/// <param name="Id">The parent's OSDU id, without a version.</param>
/// <param name="Kind">The kind the record's template names as its parent.</param>
/// <param name="Found">Whether the platform holds the parent.</param>
/// <param name="Version">The version read; null when the platform holds no parent.</param>
public sealed record ParentUsage(string Id, string Kind, bool Found, long? Version);

/// <summary>What asking the platform one question came to.</summary>
public enum SearchOutcome
{
    /// <summary>Exactly one record matches.</summary>
    Found,

    /// <summary>No record matches.</summary>
    NotFound,

    /// <summary>More than one record matches, so the value names none of them.</summary>
    Ambiguous,

    /// <summary>The platform refused the query itself, and said why.</summary>
    Refused,
}

/// <summary>The answer to one <see cref="SearchQuestion"/>.</summary>
public sealed record SearchAnswer
{
    /// <summary>How many of the matching records an ambiguous answer names; the rest are counted.</summary>
    public const int NamedCandidates = 5;

    private SearchAnswer(SearchOutcome outcome, string? id, long total, IReadOnlyList<string> candidates, string? reason)
    {
        Outcome = outcome;
        Id = id;
        Total = total;
        Candidates = candidates;
        Reason = reason;
    }

    public SearchOutcome Outcome { get; }

    /// <summary>The record found, as OSDU names it without a version.</summary>
    public string? Id { get; }

    /// <summary>How many records the platform says match.</summary>
    public long Total { get; }

    /// <summary>For an ambiguous answer, the ids of the first matches the platform returned, at most <see cref="NamedCandidates"/>.</summary>
    public IReadOnlyList<string> Candidates { get; }

    /// <summary>For a refused query, what the platform said.</summary>
    public string? Reason { get; }

    /// <summary>No record matches.</summary>
    public static SearchAnswer None { get; } = new(SearchOutcome.NotFound, null, 0, [], null);

    /// <summary>Exactly one record matches: <paramref name="id"/>.</summary>
    public static SearchAnswer Found(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new SearchAnswer(SearchOutcome.Found, id, 1, [], null);
    }

    /// <summary><paramref name="total"/> records match, of which <paramref name="candidates"/> are the first returned.</summary>
    public static SearchAnswer Ambiguous(long total, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, 2);
        return new SearchAnswer(SearchOutcome.Ambiguous, null, total, candidates.Take(NamedCandidates).ToList(), null);
    }

    /// <summary>The platform refused the query, saying <paramref name="reason"/>.</summary>
    public static SearchAnswer Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SearchAnswer(SearchOutcome.Refused, null, 0, [], reason);
    }

    /// <summary>What the answer says, as a hold reason quotes it.</summary>
    public string Describe() => Outcome switch
    {
        SearchOutcome.Found => $"found {Id}",
        SearchOutcome.NotFound => "found no record",
        SearchOutcome.Ambiguous => $"found {Total} records ({string.Join(", ", Candidates)}{(Total > Candidates.Count ? $" and {Total - Candidates.Count} more" : string.Empty)})",
        _ => $"was refused by the search service: {Reason}",
    };
}

/// <summary>
/// One question a render consulted and the answer it got, kept so a record says how every reference it carries was
/// resolved, the way a cache usage does.
/// </summary>
/// <param name="Kind">The kind searched.</param>
/// <param name="Field">The property compared.</param>
/// <param name="Value">What it was compared with.</param>
/// <param name="Query">The query sent.</param>
/// <param name="Outcome">What the platform answered.</param>
/// <param name="Id">The record found, when exactly one was.</param>
public sealed record SearchUsage(string Kind, string Field, string Value, string Query, SearchOutcome Outcome, string? Id);

/// <summary>
/// A search for a render that must not reach the platform: every question is answered as found-nothing, and nothing is
/// ever asked. For a render of a mapping's shape rather than of a record, where no platform is in play and no reference
/// is expected to resolve. A delivery is never given this.
/// </summary>
public sealed class NoRecordSearch : IRecordSearch
{
    public static NoRecordSearch Instance { get; } = new();

    public bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer)
    {
        answer = SearchAnswer.None;
        return true;
    }

    public bool TryRead(ParentQuestion question, [NotNullWhen(true)] out ParentAnswer? answer)
    {
        answer = ParentAnswer.None;
        return true;
    }

    public Task AnswerAsync(IReadOnlyCollection<PlatformQuestion> questions, CancellationToken ct = default)
        => Task.CompletedTask;
}
