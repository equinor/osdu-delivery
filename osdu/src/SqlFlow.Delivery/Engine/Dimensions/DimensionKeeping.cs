using System.Text;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a text read for an attribute or an element's field is kept as, by its <see cref="DimensionValueKeep"/>.</summary>
public static class DimensionKeeping
{
    /// <summary>
    /// <paramref name="text"/> as it is kept: as a value shows it, trimmed unless <paramref name="trim"/> says otherwise, cut
    /// at <paramref name="longest"/>; or as a key, a record's id or a reference, always trimmed and left out when longer,
    /// since a key cut joins to nothing. Null when nothing is kept.
    /// </summary>
    /// <returns>The value kept, whether it was cut, and whether it was left out for its length.</returns>
    public static (string? Value, bool Cut, bool TooLong) Keep(string text, DimensionValueKeep keep, int longest, bool trim = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var kept = keep switch
        {
            DimensionValueKeep.Key => trimmed,
            DimensionValueKeep.Id => TargetId.IsRecordReference(trimmed) ? TargetId.WithoutVersion(trimmed) : trimmed,
            DimensionValueKeep.Reference => TargetId.IsRecordReference(trimmed) && trimmed.EndsWith(':') ? trimmed[..^1] : trimmed,
            _ when !trim => TargetId.IsRecordReference(trimmed) ? DimensionLabeler.DisplayOf(trimmed) : text,
            _ => DimensionLabeler.DisplayOf(trimmed).Trim(),
        };

        if (kept.Length == 0)
        {
            return (null, false, false);
        }

        if (kept.Length <= longest)
        {
            return (kept, false, false);
        }

        return keep == DimensionValueKeep.Value ? (kept[..longest], true, false) : (null, false, true);
    }

    /// <summary>
    /// The value <paramref name="texts"/> come to, in their order, each decoded by <paramref name="decode"/> and kept by
    /// <paramref name="keep"/> whole: the first kept, every one kept joined by <see cref="DimensionElementField.Separator"/>
    /// (each once, or as often as it is held), or the greatest (<see cref="CompareCodePoints"/>), as <paramref name="many"/>
    /// says. The value is then cut at <paramref name="longest"/> as <see cref="Keep"/> cuts one, or left out for its length,
    /// so a key or an id kept is never cut part way. A value of white space alone is no value unless
    /// <paramref name="trim"/> is false, when it is kept as it is held.
    /// </summary>
    /// <returns>
    /// The value kept; the place in <paramref name="texts"/> of the text it was read from (the first joined), -1 for none;
    /// whether it was cut; and whether a value was left out for its length.
    /// </returns>
    public static (string? Value, int From, bool Cut, bool TooLong) KeepMany(
        IReadOnlyList<string> texts, DimensionValueKeep keep, DimensionValueMany many, DimensionValueDecode decode, int longest, bool trim = true)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var kept = new List<(string Text, int At)>();
        var tooLong = false;
        for (var at = 0; at < texts.Count; at++)
        {
            if (!IsValue(texts[at], keep, trim))
            {
                continue;
            }

            var (one, _, over) = Keep(Decoded(texts[at], decode), keep, int.MaxValue, trim);
            tooLong |= over;
            if (one is null || (many != DimensionValueMany.All && kept.Exists(k => string.Equals(k.Text, one, StringComparison.Ordinal))))
            {
                continue;
            }

            kept.Add((one, at));
            if (many == DimensionValueMany.First)
            {
                break;
            }
        }

        if (kept.Count == 0)
        {
            return (null, -1, false, tooLong);
        }

        var (value, from) = many switch
        {
            DimensionValueMany.Join or DimensionValueMany.All => (string.Join(DimensionElementField.Separator, kept.Select(k => k.Text)), kept[0].At),
            DimensionValueMany.Max => kept.Aggregate((best, next) => CompareCodePoints(next.Text, best.Text) > 0 ? next : best),
            _ => kept[0],
        };

        if (value.Length <= longest)
        {
            return (value, from, false, false);
        }

        return keep == DimensionValueKeep.Value ? (value[..longest], from, true, false) : (null, -1, false, true);
    }

    /// <summary>
    /// <paramref name="a"/> against <paramref name="b"/> by Unicode code point, which is the order of their UTF-8 bytes: what
    /// SQL Server's binary collations and Spark order text by. An ordinal comparison of UTF-16 would put a character past
    /// U+FFFF (two surrogates) before one from U+E000 to U+FFFF.
    /// </summary>
    public static int CompareCodePoints(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x == y)
            {
                continue;
            }

            var (xPast, yPast) = (char.IsSurrogate(x), char.IsSurrogate(y));
            return xPast == yPast ? x.CompareTo(y) : xPast ? 1 : -1;
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a value a path reached: not empty, and not white space alone unless it is kept as a
    /// value untrimmed.
    /// </summary>
    public static bool IsValue(string? text, DimensionValueKeep keep, bool trim)
        => trim || keep != DimensionValueKeep.Value ? !string.IsNullOrWhiteSpace(text) : !string.IsNullOrEmpty(text);

    /// <summary><paramref name="text"/> decoded by <paramref name="decode"/>.</summary>
    public static string Decoded(string text, DimensionValueDecode decode)
        => decode == DimensionValueDecode.Url ? UrlDecoded(text) : text;

    /// <summary>
    /// <paramref name="text"/> with its percent-escapes decoded, each run of escapes read as UTF-8 (a byte sequence that is
    /// no UTF-8 becomes U+FFFD). A <c>+</c> stays a <c>+</c>, and a text holding a <c>%</c> that two hexadecimal digits do not
    /// follow is kept as it is: what Spark's <c>try_url_decode</c> gives a text whose <c>+</c> is escaped first, falling back
    /// to the text where it fails.
    /// </summary>
    public static string UrlDecoded(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var decoded = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] != '%')
            {
                decoded.Append(text[i]);
                i++;
                continue;
            }

            bytes.Clear();
            while (i < text.Length && text[i] == '%')
            {
                if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2]))
                {
                    return text;
                }

                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 3;
            }

            decoded.Append(Encoding.UTF8.GetString(bytes.ToArray()));
        }

        return decoded.ToString();
    }

    /// <summary>How a document writes <paramref name="keep"/>.</summary>
    public static string Named(DimensionValueKeep keep) => keep switch
    {
        DimensionValueKeep.Key => "key",
        DimensionValueKeep.Id => "id",
        DimensionValueKeep.Reference => "reference",
        _ => "value",
    };

    /// <summary>The keep a document names, ignoring case; null when it names none of them.</summary>
    public static DimensionValueKeep? Parse(string? named) => named?.Trim().ToLowerInvariant() switch
    {
        "value" => DimensionValueKeep.Value,
        "key" => DimensionValueKeep.Key,
        "id" => DimensionValueKeep.Id,
        "reference" => DimensionValueKeep.Reference,
        _ => null,
    };

    /// <summary>How a document writes <paramref name="many"/>.</summary>
    public static string Named(DimensionValueMany many) => many switch
    {
        DimensionValueMany.Join => "join",
        DimensionValueMany.All => "all",
        DimensionValueMany.Max => "max",
        _ => "first",
    };

    /// <summary>The many a document names, ignoring case; null when it names none of them.</summary>
    public static DimensionValueMany? ParseMany(string? named) => named?.Trim().ToLowerInvariant() switch
    {
        "first" => DimensionValueMany.First,
        "join" => DimensionValueMany.Join,
        "all" => DimensionValueMany.All,
        "max" => DimensionValueMany.Max,
        _ => null,
    };

    /// <summary>How a document writes <paramref name="decode"/>.</summary>
    public static string Named(DimensionValueDecode decode) => decode == DimensionValueDecode.Url ? "url" : "none";

    /// <summary>The decode a document names, ignoring case; null when it names none of them.</summary>
    public static DimensionValueDecode? ParseDecode(string? named) => named?.Trim().ToLowerInvariant() switch
    {
        "none" => DimensionValueDecode.None,
        "url" => DimensionValueDecode.Url,
        _ => null,
    };
}
