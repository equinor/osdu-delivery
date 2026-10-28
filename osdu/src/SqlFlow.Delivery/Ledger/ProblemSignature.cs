using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What kind of problem keeps a record held or failed, told apart from the record it happened to
/// (osdu/docs/reference/concepts/record-lifecycle.md, Issues). A record's error names the record: the value it read, an
/// OSDU id, a moment, a count, a file, the correlation id of the request that was refused. The pattern is the error
/// with those parts replaced by placeholders, so the errors of a million records refused for the same reason read
/// alike, and its hash is what the ledger groups blocked records by.
/// </summary>
/// <remarks>
/// This is the one place a problem is decided: the intake's holds, the worker's failures, the backfill of records held
/// before the ledger kept problems, and every read that names a group compute it here, from the redacted error alone, so
/// the same error is the same problem on every path. What is replaced, in order of precedence:
/// <list type="bullet">
/// <item>the correlation id a refused request names, whatever the service that answered spells it with, becomes <c>&lt;id&gt;</c>;</item>
/// <item>an OSDU kind (<c>osdu:wks:master-data--Wellbore:1.0.0</c>) is kept as it is: it names what was refused, not which record;</item>
/// <item>a web address keeps its scheme, host and the words of its path, and each other segment becomes <c>&lt;id&gt;</c>, so
/// the service that refused stays and the record's id in the path goes; any other address (a lake, a seismic store) and a
/// file path become <c>&lt;path&gt;</c>;</item>
/// <item>a moment (ISO 8601, a cache version's label, a time of day) becomes <c>&lt;time&gt;</c>;</item>
/// <item>a UUID (a delivery key, a correlation id) becomes <c>&lt;id&gt;</c>, and an OSDU record id <c>&lt;osdu id&gt;</c>;</item>
/// <item>a run of sixteen or more hexadecimal characters (a hash) becomes <c>&lt;hash&gt;</c>;</item>
/// <item>a value in single, back or typographic quotes becomes <c>'&lt;value&gt;'</c> in its own quotes, and an escaped double
/// quoted value inside a quoted answer <c>\"&lt;value&gt;\"</c>; a double quoted string is kept, because that is how a service's
/// answer spells its reason and its message;</item>
/// <item>a number standing alone becomes <c>&lt;n&gt;</c>: a row, an index, a count, a version. A digit inside a word
/// (<c>Tag4</c>, <c>v2</c>) is part of the word.</item>
/// </list>
/// Whitespace runs collapse to one space. Two errors that differ in a part the rules keep are two problems; that splits a
/// problem rather than merging two, which is the safer way to be wrong when a whole group is released at once.
/// </remarks>
public static partial class ProblemSignature
{
    /// <summary>The longest pattern kept, which is the length of the error it is made from.</summary>
    public const int MaxPatternLength = 2000;

    /// <summary>The pattern of a held or failed record that recorded no reason: its problem is that nothing says why.</summary>
    public const string NoReason = "no reason recorded";

    /// <summary>How a problem's hash is written in a URL, a command line and an answer: sixteen lowercase hexadecimal characters.</summary>
    public const int TextLength = 16;

    /// <summary>The error with every part that names the record replaced by a placeholder; <see cref="NoReason"/> for none.</summary>
    public static string Pattern(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return NoReason;
        }

        // The rules are linear in what they read, and an error is never longer than the column that keeps it.
        var bounded = error.Length > MaxPatternLength ? error[..MaxPatternLength] : error;
        var masked = Parts().Replace(bounded, Mask);
        var collapsed = Whitespace().Replace(masked, " ").Trim();
        if (collapsed.Length == 0)
        {
            return NoReason;
        }

        return collapsed.Length > MaxPatternLength ? collapsed[..MaxPatternLength] : collapsed;
    }

    /// <summary>
    /// The values an error names that its pattern replaces with <c>'&lt;value&gt;'</c>: what the record read from its row,
    /// in the order the error names them, without their quotes. Two records of one problem naming the same values carry
    /// the same error, as every record of a set the same mistake was made in does; records naming different values each
    /// carry their own row's.
    /// </summary>
    public static IReadOnlyList<string> Values(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return [];
        }

        var bounded = error.Length > MaxPatternLength ? error[..MaxPatternLength] : error;
        var values = new List<string>();
        foreach (Match match in Parts().Matches(bounded))
        {
            foreach (var name in ValueGroups)
            {
                var group = match.Groups[name];
                if (group.Success)
                {
                    // The quotes the value stood in, one or two characters a side, are not the value.
                    var quote = name == "escaped" ? 2 : 1;
                    values.Add(group.Value[quote..^quote]);
                    break;
                }
            }
        }

        return values;
    }

    /// <summary>
    /// What errors of one problem say of where it lies: <see cref="ProblemShape.Set"/> when they name no value a record
    /// read, or every one names the same values, so the same mistake is in every record; <see cref="ProblemShape.Rows"/>
    /// when they name different values, each its own row's.
    /// </summary>
    public static ProblemShape ShapeOf(IEnumerable<string?> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        IReadOnlyList<string>? first = null;
        foreach (var error in errors)
        {
            var values = Values(error);
            if (first is null)
            {
                first = values;
            }
            else if (!first.SequenceEqual(values, StringComparer.Ordinal))
            {
                return ProblemShape.Rows;
            }
        }

        return ProblemShape.Set;
    }

    /// <summary>
    /// The problem as <paramref name="errors"/>, errors of its records, say it: <paramref name="pattern"/> with each part every
    /// one of them names alike written as they name it, and the placeholder kept where they differ. A set error's records
    /// say the same values, numbers and moments, so it reads as they wrote it; row errors keep a placeholder at each value
    /// their rows differ in. Two records can share a part by chance, so the more records are read the surer it is: a
    /// problem's newest and oldest say it for a listing, samples spread across it for the problem's own page. An error of
    /// another problem is passed over, and with none left the pattern is what is said.
    /// </summary>
    public static string Shared(string pattern, IEnumerable<string?> errors)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(errors);
        List<string>? between = null;
        List<(string Masked, string Said)>? parts = null;
        var alike = new List<bool>();
        foreach (var error in errors)
        {
            if (string.IsNullOrWhiteSpace(error) || !string.Equals(Pattern(error), pattern, StringComparison.Ordinal))
            {
                continue;
            }

            var (text, said) = Read(error);
            if (parts is null)
            {
                (between, parts) = (text, said);
                alike.AddRange(said.Select(_ => true));
                continue;
            }

            // An error read into other parts though it makes the same pattern (a placeholder written in its own words) says
            // nothing part by part.
            if (said.Count != parts.Count)
            {
                continue;
            }

            for (var i = 0; i < said.Count; i++)
            {
                alike[i] = alike[i] && string.Equals(said[i].Said, parts[i].Said, StringComparison.Ordinal);
            }
        }

        if (parts is null || between is null)
        {
            return pattern;
        }

        var builder = new StringBuilder(pattern.Length);
        for (var i = 0; i < parts.Count; i++)
        {
            builder.Append(between[i]).Append(alike[i] ? parts[i].Said : parts[i].Masked);
        }

        builder.Append(between[^1]);
        var collapsed = Whitespace().Replace(builder.ToString(), " ").Trim();
        return collapsed.Length > MaxPatternLength ? collapsed[..MaxPatternLength] : collapsed;
    }

    /// <summary>
    /// An error read as its pattern reads it: the text between the parts that name the record, and each part as the pattern
    /// writes it and as the error said it.
    /// </summary>
    private static (List<string> Between, List<(string Masked, string Said)> Parts) Read(string error)
    {
        var bounded = error.Length > MaxPatternLength ? error[..MaxPatternLength] : error;
        var between = new List<string>();
        var parts = new List<(string Masked, string Said)>();
        var at = 0;
        foreach (Match match in Parts().Matches(bounded))
        {
            between.Add(bounded[at..match.Index]);
            parts.Add((Mask(match), match.Value));
            at = match.Index + match.Length;
        }

        between.Add(bounded[at..]);
        return (between, parts);
    }

    /// <summary>Whether a pattern holds a value a record read from its row (<see cref="Values"/>).</summary>
    public static bool NamesValues(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Contains("<value>", StringComparison.Ordinal);
    }

    /// <summary>The groups of <see cref="Parts"/> that stand for a value the record read.</summary>
    private static readonly string[] ValueGroups = ["single", "back", "curly", "escaped"];

    /// <summary>The hash of a pattern: the first eight bytes of its SHA-256, big-endian.</summary>
    public static long Hash(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(pattern), digest);
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }

    /// <summary>The problem of a held or failed record whose error is <paramref name="error"/>.</summary>
    public static long Of(string? error) => Hash(Pattern(error));

    /// <summary>A problem's hash as text: sixteen lowercase hexadecimal characters.</summary>
    public static string Format(long hash) => unchecked((ulong)hash).ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Reads a problem's hash written by <see cref="Format"/>; false for anything else.</summary>
    public static bool TryParse(string? text, out long hash)
    {
        hash = 0;
        if (text is null || text.Length != TextLength || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        hash = unchecked((long)value);
        return true;
    }

    private static string Mask(Match match)
    {
        var groups = match.Groups;
        if (groups["correlation"].Success)
        {
            return "<id>";
        }

        if (groups["kind"].Success)
        {
            return match.Value;
        }

        if (groups["web"].Success)
        {
            return Web(groups["web"].Value);
        }

        if (groups["path"].Success)
        {
            return "<path>";
        }

        if (groups["time"].Success)
        {
            return "<time>";
        }

        if (groups["uuid"].Success)
        {
            return "<id>";
        }

        if (groups["osdu"].Success)
        {
            return "<osdu id>";
        }

        if (groups["hash"].Success)
        {
            return "<hash>";
        }

        if (groups["single"].Success)
        {
            return "'<value>'";
        }

        if (groups["back"].Success)
        {
            return "`<value>`";
        }

        if (groups["curly"].Success)
        {
            return "“<value>”";
        }

        if (groups["escaped"].Success)
        {
            return "\\\"<value>\\\"";
        }

        return "<n>";
    }

    /// <summary>
    /// A web address as a problem names it: the scheme, the host and the words of the path, which say what was called, with
    /// every other segment of the path (an id, a number, an escaped value) as <c>&lt;id&gt;</c> and the query left out.
    /// </summary>
    private static string Web(string address)
    {
        var query = address.IndexOfAny(['?', '#']);
        var bare = query < 0 ? address : address[..query];
        var start = bare.IndexOf("://", StringComparison.Ordinal) + 3;
        var slash = bare.IndexOf('/', start);
        if (slash < 0)
        {
            return query < 0 ? bare : bare + "?<query>";
        }

        var builder = new StringBuilder(bare.Length);
        builder.Append(bare, 0, slash);
        foreach (var segment in bare[(slash + 1)..].Split('/'))
        {
            builder.Append('/');
            builder.Append(segment.Length == 0 || PathWord().IsMatch(segment) ? segment : "<id>");
        }

        if (query >= 0)
        {
            builder.Append("?<query>");
        }

        return builder.ToString();
    }

    // One pass over the error: at each place the first rule that matches there wins, so a kind is never read as an id, an
    // address never as a path, a moment never as numbers. A replaced part is not read again.
    [GeneratedRegex(
        """
        (?<correlation>(?<=\(correlation-id\s)[^\s)]+)
        |(?<kind>\b[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+:\d+\.\d+\.\d+\b)
        |(?<web>\bhttps?://[^\s'"`<>()\[\]{}]+)
        |(?<path>(?:\b[A-Za-z][A-Za-z0-9+.-]*://[^\s'"`<>]+)|(?:\b[A-Za-z]:\\[^\s'"`<>|]*)|(?:\\\\[^\s'"`<>|]+)|(?:(?<![\w.~-])/(?:[\w.@~-]+/)+[\w.@*?~-]*))
        |(?<time>\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?\b|\b\d{8}T\d{6}Z(?:-\d+)?\b|\b\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\b)
        |(?<uuid>\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b|\b[0-9A-Fa-f]{32}\b)
        |(?<osdu>\b[A-Za-z0-9_.-]+:[A-Za-z0-9_.]+(?:-[A-Za-z0-9_.]+)*--[A-Za-z0-9_.]+:[^\s'"`,;()\[\]{}<>]*)
        |(?<hash>\b(?=[0-9]*[A-Fa-f])[0-9A-Fa-f]{16,}\b)
        |(?<single>(?<![\p{L}\p{N}])'[^'\r\n]{1,1000}'(?![\p{L}\p{N}]))
        |(?<back>`[^`\r\n]{1,1000}`)
        |(?<curly>“[^”\r\n]{1,1000}”)
        |(?<escaped>\\"[^"\\\r\n]{1,1000}\\")
        |(?<number>(?<![\w.])\d+(?:\.\d+)*(?![\w]))
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex Parts();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>A segment of a web address's path that says what is called: a word, never an id or a number.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathWord();
}

/// <summary>Where a problem lies, as the errors of its records say (<see cref="ProblemSignature.ShapeOf"/>).</summary>
public enum ProblemShape
{
    /// <summary>
    /// The same error in every record looked at: a set error, made once for the whole set (a dataset, a cache entry, a
    /// mapping, a legal tag), fixed once, after which every record of the problem is released together.
    /// </summary>
    Set,

    /// <summary>
    /// The records name different values, each its own row's: row errors, fixed in the rows themselves; a corrected row is
    /// planned again on its own.
    /// </summary>
    Rows,
}
