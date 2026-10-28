using System.Globalization;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Where one of a record's identity tokens came from. The kind is shown beside a hit, so an operator knows whether they
/// matched a well name, an OSDU id or the file a record arrived in, and it lets a lookup prefer one over another.
/// </summary>
public enum RecordIdentityKind
{
    /// <summary>A value of a column the mapping declares in <c>dataset.identity</c>: a wellbore id, a well name.</summary>
    Declared,

    /// <summary>A value of one of the key columns the delivery key is derived from.</summary>
    Key,

    /// <summary>A word of the record's rendered label.</summary>
    Label,

    /// <summary>The OSDU id, and the part of it after the last colon.</summary>
    Target,

    /// <summary>The ingestion file the record's current version came from.</summary>
    File,

    /// <summary>The OSDU id of a record an enrichment of the record's mapping adds to it, and the part of it after the last colon.</summary>
    Added,
}

/// <summary>What bounds the identity index, so a record costs a known number of small rows however wide its source row is.</summary>
public static class RecordIdentityLimits
{
    /// <summary>The longest token stored, which is the column's length. Longer values are cut; a prefix search still finds them by their start.</summary>
    public const int MaxTokenLength = DeliveryModel.MaxIdentityTokenLength;

    /// <summary>The most tokens one record contributes. A row with more identifying columns than this keeps the first.</summary>
    public const int MaxPerRecord = 24;

    /// <summary>The shortest token stored: one or two characters match too much to be worth an index row.</summary>
    public const int MinTokenLength = 3;

    /// <summary>The most words taken from a label, in order; the rest are found by the label's own prefix index.</summary>
    public const int MaxLabelWords = 8;

    /// <summary>
    /// The most tokens the records an enrichment adds contribute, beside <see cref="MaxPerRecord"/>: each added record's id
    /// and its trailing part, so a record adding many is found by the first of them.
    /// </summary>
    public const int MaxAddedPerRecord = 64;
}

/// <summary>
/// One identity token of a record: a value an operator may hold and search by, in the form the index stores it.
/// Tokens are compared case-insensitively, so they are stored folded, with the value as written kept for display.
/// </summary>
public readonly record struct RecordIdentityToken(string Token, string Display, RecordIdentityKind Kind);

/// <summary>
/// Turns what the ledger knows about a record into the tokens its identity index holds: the declared identity columns,
/// the key values, the words of the label, the OSDU id with its trailing part, and the ingestion file name. This is the
/// one place the token set is decided, so the writer, the backfill and the tests agree on what a record is findable by.
/// </summary>
public static class RecordIdentities
{
    /// <summary>The characters a label is split on: a label is written for people, so its separators are punctuation.</summary>
    private static readonly char[] LabelSeparators = [' ', '\t', '/', '\\', ',', ';', '|', '(', ')', '[', ']', '{', '}', '"', '\''];

    /// <summary>
    /// Every token a record is findable by, folded, de-duplicated and capped. Order decides what survives the cap:
    /// the declared identities first, then the source key and its parts, the OSDU id, the label's words and the file
    /// name. The ids of the records its enrichments add (<paramref name="added"/>) follow under a cap of their own, so a
    /// record is found by them as well as by its own.
    /// </summary>
    public static IReadOnlyList<RecordIdentityToken> Of(
        IEnumerable<string>? declared,
        string? sourceKey,
        IEnumerable<string?>? keyValues,
        string? label,
        string? targetId,
        string? sourceFileName,
        IEnumerable<string>? added = null)
    {
        var tokens = new List<RecordIdentityToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in declared ?? [])
        {
            Add(tokens, seen, value, RecordIdentityKind.Declared);
        }

        // The source key whole, then its parts: an operator pastes the key as the ledger prints it as often as they
        // hold one column of it, and both find the record.
        Add(tokens, seen, sourceKey, RecordIdentityKind.Key);
        foreach (var value in keyValues ?? [])
        {
            Add(tokens, seen, value, RecordIdentityKind.Key);
        }

        if (targetId is { Length: > 0 })
        {
            Add(tokens, seen, targetId, RecordIdentityKind.Target);
            // The id's own part, which is what an operator reads off a record in OSDU and pastes back here.
            var at = targetId.LastIndexOf(':');
            if (at >= 0 && at < targetId.Length - 1)
            {
                Add(tokens, seen, targetId[(at + 1)..], RecordIdentityKind.Target);
            }
        }

        var words = 0;
        foreach (var word in (label ?? string.Empty).Split(LabelSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (words >= RecordIdentityLimits.MaxLabelWords)
            {
                break;
            }

            if (Add(tokens, seen, word, RecordIdentityKind.Label))
            {
                words++;
            }
        }

        Add(tokens, seen, sourceFileName, RecordIdentityKind.File);
        var own = tokens.Count <= RecordIdentityLimits.MaxPerRecord ? tokens : tokens[..RecordIdentityLimits.MaxPerRecord];

        // What the record's enrichments add, by its id and the id's own part, as the record's own id is found.
        var others = new List<RecordIdentityToken>();
        foreach (var id in added ?? [])
        {
            if (others.Count >= RecordIdentityLimits.MaxAddedPerRecord)
            {
                break;
            }

            AddUnder(others, seen, id, RecordIdentityKind.Added, RecordIdentityLimits.MaxAddedPerRecord);
            var at = id.LastIndexOf(':');
            if (at >= 0 && at < id.Length - 1)
            {
                AddUnder(others, seen, id[(at + 1)..], RecordIdentityKind.Added, RecordIdentityLimits.MaxAddedPerRecord);
            }
        }

        return [.. own, .. others];
    }

    /// <summary>The key values a record's <c>SourceKeyJson</c> holds; empty when it holds none or is not a JSON array.</summary>
    public static IReadOnlyList<string> KeyValues(string? sourceKeyJson)
    {
        if (string.IsNullOrWhiteSpace(sourceKeyJson))
        {
            return [];
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(sourceKeyJson);
            return node switch
            {
                System.Text.Json.Nodes.JsonArray array => array.Select(Text).OfType<string>().ToList(),
                System.Text.Json.Nodes.JsonObject map => map.Select(pair => Text(pair.Value)).OfType<string>().ToList(),
                _ => [],
            };
        }
        catch (System.Text.Json.JsonException)
        {
            // A record whose key tuple was stored before it was JSON, or by another writer: it is still findable by
            // everything else, so an unreadable tuple contributes nothing rather than failing the write.
            return [];
        }
    }

    /// <summary>How a term is folded before it is compared with a stored token.</summary>
    public static string Fold(string value) => value.Trim().ToUpperInvariant();

    private static string? Text(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        null => null,
        System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) => text,
        System.Text.Json.Nodes.JsonValue value => value.ToJsonString().Trim('"'),
        _ => null,
    };

    /// <summary>Adds one token when it is worth an index row and is not already held; says whether it was added.</summary>
    private static bool Add(List<RecordIdentityToken> tokens, HashSet<string> seen, string? value, RecordIdentityKind kind)
        => AddUnder(tokens, seen, value, kind, RecordIdentityLimits.MaxPerRecord);

    /// <summary>Adds one token when <paramref name="tokens"/> holds fewer than <paramref name="cap"/> and it is worth an index row and not already held.</summary>
    private static bool AddUnder(List<RecordIdentityToken> tokens, HashSet<string> seen, string? value, RecordIdentityKind kind, int cap)
    {
        if (value is null || tokens.Count >= cap)
        {
            return false;
        }

        var display = value.Trim();
        if (display.Length < RecordIdentityLimits.MinTokenLength)
        {
            return false;
        }

        if (display.Length > RecordIdentityLimits.MaxTokenLength)
        {
            display = display[..RecordIdentityLimits.MaxTokenLength];
        }

        var token = Fold(display);
        if (!seen.Add(token))
        {
            return false;
        }

        tokens.Add(new RecordIdentityToken(token, display, kind));
        return true;
    }

    /// <summary>The kind's wire and storage text, which is what the API and the GUI name a hit by.</summary>
    public static string Text(RecordIdentityKind kind) => kind switch
    {
        RecordIdentityKind.Declared => "identity",
        RecordIdentityKind.Key => "key",
        RecordIdentityKind.Label => "label",
        RecordIdentityKind.Target => "osdu",
        RecordIdentityKind.File => "file",
        RecordIdentityKind.Added => "added",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown identity kind"),
    };

    /// <summary>Reads back what <see cref="Text(RecordIdentityKind)"/> wrote; an unknown text reads as a declared identity.</summary>
    public static RecordIdentityKind KindOf(string text) => text switch
    {
        "identity" => RecordIdentityKind.Declared,
        "key" => RecordIdentityKind.Key,
        "label" => RecordIdentityKind.Label,
        "osdu" => RecordIdentityKind.Target,
        "file" => RecordIdentityKind.File,
        "added" => RecordIdentityKind.Added,
        _ => RecordIdentityKind.Declared,
    };

    /// <summary>A record's tokens as one line, for a log or a test failure.</summary>
    public static string Describe(IReadOnlyList<RecordIdentityToken> tokens)
        => tokens.Count == 0
            ? "(none)"
            : string.Join(", ", tokens.Select(t => string.Create(CultureInfo.InvariantCulture, $"{Text(t.Kind)}:{t.Display}")));
}
