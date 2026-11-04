using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>What a key of the curve a log's rows are indexed by is: a number (a depth), an instant (a time), or other text.</summary>
internal enum CurveKeyKind
{
    Number,
    Instant,
    Text,
}

/// <summary>
/// A key of the curve a log's rows are indexed by (osdu/docs/reference/flow/curve-proof-values.md, Values at a key), as an
/// <c>at</c> proof value names it and as a row holds it: a number compared as written, in decimal, so 1500.1524 in the document
/// is 1500.1524 in the bulk data whatever binary value either holds, and 1500 is 1500.0; an ISO 8601 time as the instant it
/// names; other text as it is.
/// </summary>
/// <param name="Kind">What the key is.</param>
/// <param name="Order">The number, or the instant's ticks: what keys of one kind order by. Zero for text.</param>
/// <param name="Text">The text of a key that is neither a number nor a time; empty for the others.</param>
internal readonly record struct CurveKey(CurveKeyKind Kind, decimal Order, string Text)
{
    /// <summary>The key a document's value names, or null for one that names no key (true, false, nothing).</summary>
    public static CurveKey? Of(ExpectedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind is ExpectedValueKind.Number or ExpectedValueKind.Text ? FromText(value.Text) : null;
    }

    /// <summary>The key a row's index holds, or null when it holds none.</summary>
    public static CurveKey? Of(JsonNode? value)
    {
        if (RecordValues.TryNumber(value, out var number))
        {
            return FromText(RecordValues.Format(number));
        }

        return RecordValues.TryText(value, out var text) ? FromText(text) : null;
    }

    private static CurveKey FromText(string written)
    {
        var text = written.Trim();
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return new CurveKey(CurveKeyKind.Number, number, string.Empty);
        }

        if (RecordValues.TryInstant(text, out var instant))
        {
            return new CurveKey(CurveKeyKind.Instant, instant.UtcTicks, string.Empty);
        }

        // A number beyond what a decimal holds keeps the text a double writes it as, so two writings of it still meet.
        return RecordValues.TryParseNumber(text, out var large)
            ? new CurveKey(CurveKeyKind.Text, 0, RecordValues.Format(large))
            : new CurveKey(CurveKeyKind.Text, 0, text);
    }

    /// <summary>Whether keys of this kind order, so a key no row holds can be placed between the rows either side of it.</summary>
    public bool Ordered => Kind != CurveKeyKind.Text;
}

/// <summary>The rows whose index holds one key, the value each curve asked at it holds in the first of them, and, when none does, the rows either side of it.</summary>
internal sealed class CurveKeyHits
{
    /// <summary>The rows a reason names when a key is held by several.</summary>
    public const int NamedRows = 5;

    public long Count { get; set; }

    /// <summary>The first rows holding the key, at most <see cref="NamedRows"/>.</summary>
    public List<long> Rows { get; } = [];

    /// <summary>Each curve's value in the first row holding the key; null where the curve has none there.</summary>
    public Dictionary<string, JsonNode?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>The index and row just before the key, of the first two rows in a row the key lies between, when no row holds it.</summary>
    public (JsonNode Value, long Row)? Before { get; set; }

    public (JsonNode Value, long Row)? After { get; set; }
}

/// <summary>
/// Reads, row by row, the keys <c>at</c> proof values ask for: which rows hold each, what each curve asked at it holds there,
/// and where a key no row holds would lie, between two rows in a row; with the first and last value of the index, which a
/// reason names when a key lies outside them.
/// </summary>
internal sealed class CurveKeyReading
{
    private readonly Dictionary<CurveKey, CurveKeyHits> _hits = [];
    private readonly Dictionary<CurveKey, List<string>> _curves = [];
    private readonly CurveKey[] _ordered;
    private CurveKey? _previous;
    private JsonNode? _previousValue;
    private long _previousRow;

    public CurveKeyReading(IEnumerable<CurveCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        foreach (var check in checks.Where(c => c.Measure == CurveMeasure.At))
        {
            var key = Key(check);
            if (!_hits.ContainsKey(key))
            {
                _hits[key] = new CurveKeyHits();
                _curves[key] = [];
            }

            if (!_curves[key].Contains(check.Curve, StringComparer.Ordinal))
            {
                _curves[key].Add(check.Curve);
            }
        }

        _ordered = _hits.Keys.Where(k => k.Ordered).OrderBy(k => k.Kind).ThenBy(k => k.Order).ToArray();
    }

    /// <summary>Whether any proof value asks for a key, so rows are read for them.</summary>
    public bool Asks => _hits.Count > 0;

    /// <summary>The index's first and last value, and their rows.</summary>
    public (JsonNode Value, long Row)? First { get; private set; }

    public (JsonNode Value, long Row)? Last { get; private set; }

    /// <summary>The key a proof value asks for.</summary>
    public static CurveKey Key(CurveCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Key is { } written && CurveKey.Of(written) is { } key
            ? key
            : throw new InvalidOperationException($"The proof value {check.Label} names no key; the document is refused without one.");
    }

    /// <summary>What was read of a key.</summary>
    public CurveKeyHits Hits(CurveKey key) => _hits.TryGetValue(key, out var hits) ? hits : new CurveKeyHits();

    /// <summary>One row: its index value, its position from 1, and the value it holds of a curve.</summary>
    public void Observe(JsonNode? index, long row, Func<string, JsonNode?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);
        if (index is null || CurveKey.Of(index) is not { } key)
        {
            return;
        }

        First ??= (index, row);
        Last = (index, row);
        if (_hits.TryGetValue(key, out var hits))
        {
            hits.Count++;
            if (hits.Rows.Count < CurveKeyHits.NamedRows)
            {
                hits.Rows.Add(row);
            }

            if (hits.Count == 1)
            {
                foreach (var curve in _curves[key])
                {
                    hits.Values[curve] = valueOf(curve);
                }
            }
        }

        if (key.Ordered && _previous is { } previous && previous.Kind == key.Kind && previous.Order != key.Order)
        {
            var (low, high) = previous.Order < key.Order ? (previous.Order, key.Order) : (key.Order, previous.Order);
            for (var i = LowerBound(key.Kind, low); i < _ordered.Length && _ordered[i].Kind == key.Kind && _ordered[i].Order < high; i++)
            {
                var between = _hits[_ordered[i]];
                if (_ordered[i].Order > low && between.Before is null)
                {
                    between.Before = (_previousValue!, _previousRow);
                    between.After = (index, row);
                }
            }
        }

        _previous = key;
        _previousValue = index;
        _previousRow = row;
    }

    /// <summary>The position of the first ordered key of <paramref name="kind"/> at or above <paramref name="order"/>.</summary>
    private int LowerBound(CurveKeyKind kind, decimal order)
    {
        int low = 0, high = _ordered.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var at = _ordered[middle];
            if (at.Kind < kind || (at.Kind == kind && at.Order < order))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
