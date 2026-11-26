using System.Globalization;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Preview;

/// <summary>
/// A key a preview was given, read: the key tuples the row may be looked up by (one, unless the text splits into the key's
/// parts in more than one way and the source has to say which it holds), how the text was read, or why it could not be.
/// </summary>
internal sealed record PreviewKey(IReadOnlyList<KeyTuple> Candidates, string How, string? Refusal)
{
    public static PreviewKey Of(KeyTuple key, string how) => new([key], how, null);

    public static PreviewKey Refused(string how, string refusal) => new([], how, refusal);
}

/// <summary>
/// Reads the key an operator names a record by into the key tuple the ingestion tables look the row up by. An operator holds
/// what the product shows them: a record's delivery key or OSDU id from a record page, its source key from the Records
/// page (<c>system:part/part</c>), or just the key's value. A record the ledger holds is found by any of those, exactly: a
/// delivery key and an OSDU id are the ledger's own, and a source key derives the delivery key the ledger holds the record
/// under. A record never planned is named by its source key or its key parts. What cannot be read says why, and how to
/// name the record instead, rather than looking up something else.
/// </summary>
internal static class PreviewKeys
{
    /// <summary>The longest key text read; no record key is longer, and a longer text is a paste of something else.</summary>
    public const int MaxKeyChars = 4000;

    /// <summary>The most ways a source key's slashes are tried as the separators of its parts before the text is refused as ambiguous.</summary>
    public const int MaxCandidates = 32;

    /// <summary>How many of the ledger's matches for an OSDU id are read to find the one that equals it.</summary>
    private const int LedgerMatches = 10;

    public static async Task<PreviewKey> ResolveAsync(
        FlowDefinition flow, MappingDefinition mapping, string partition, ILedger? ledger, string asked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(asked);
        var text = asked.Trim();
        var columns = flow.Source.Record.Key;
        if (text.Length > MaxKeyChars)
        {
            return PreviewKey.Refused(
                PreviewKeyForms.SourceKey,
                string.Create(CultureInfo.InvariantCulture, $"The key is {text.Length} characters long, more than the {MaxKeyChars} a record key can be; name the record by its source key or its delivery key."));
        }

        if (text.Any(char.IsControl))
        {
            return PreviewKey.Refused(PreviewKeyForms.SourceKey, "The key holds a control character (a tab, a line break), which no record key does; name one record, on one line.");
        }

        if (text.StartsWith('['))
        {
            return KeyParts(text, columns);
        }

        if (ledger is not null)
        {
            if (Guid.TryParse(text, CultureInfo.InvariantCulture, out var guid)
                && await ledger.GetRecordAsync(flow.Id, new DeliveryKey(guid), ct).ConfigureAwait(false) is { } byKey)
            {
                return Stored(byKey, PreviewKeyForms.DeliveryKey);
            }

            if (text.Contains(':', StringComparison.Ordinal) && await ByTargetIdAsync(flow, ledger, text, ct).ConfigureAwait(false) is { } byId)
            {
                return Stored(byId, PreviewKeyForms.OsduId);
            }
        }

        // An id of this flow's kind is what OSDU was sent; one the ledger does not hold was never planned by this flow. An id
        // made from the key's values (dataset.idFrom: key) reads back as that key; one made from the delivery key does not,
        // since the delivery key is a one-way hash of the key.
        var ownIds = $"{partition}:{mapping.EntityType}:";
        if (text.StartsWith(ownIds, StringComparison.OrdinalIgnoreCase))
        {
            if (mapping.Dataset.IdFrom == MappingIdSource.Key && TargetId.KeyValues(text, partition, mapping.EntityType, columns.Count) is { } values)
            {
                return PreviewKey.Of(new KeyTuple(values), PreviewKeyForms.OsduId);
            }

            return PreviewKey.Refused(
                PreviewKeyForms.OsduId,
                $"No record of this flow is delivered as {text}. The preview finds a record by its OSDU id only when the ledger holds it; name the record by its source key ({Columns(columns)}) instead.");
        }

        var parsed = SourceKeyText(text, mapping.Dataset.System, columns);
        if (parsed.Refusal is not null || ledger is null)
        {
            return parsed;
        }

        // The ledger holds a record under the delivery key its source key derives, and keeps the key its row was read by
        // exactly as the source spelled it. A reading of the text the ledger holds is the record meant; when it holds none,
        // the source is asked which of the readings it has.
        var held = new List<RecordState>();
        foreach (var candidate in parsed.Candidates)
        {
            if (await ledger.GetRecordAsync(flow.Id, DeliveryKey.Derive(mapping.Dataset.System, candidate.Values), ct).ConfigureAwait(false) is { SourceKeyJson: not null } record)
            {
                held.Add(record);
            }
        }

        return held.Count switch
        {
            0 => parsed,
            1 => Stored(held[0], PreviewKeyForms.SourceKey),
            _ => PreviewKey.Refused(
                PreviewKeyForms.SourceKey,
                $"'{text}' reads as the source key of {held.Count} records ({string.Join(", ", held.Select(r => r.SourceKeyJson))}); name one by its key parts as a JSON array, or by its delivery key."),
        };
    }

    /// <summary>The record of the flow delivered to exactly <paramref name="targetId"/>, when the ledger holds one.</summary>
    private static async Task<RecordState?> ByTargetIdAsync(FlowDefinition flow, ILedgerQueries ledger, string targetId, CancellationToken ct)
    {
        try
        {
            var matches = await ledger.ListAsync(flow.Id, new RecordQuery { Search = targetId, Max = LedgerMatches }, ct).ConfigureAwait(false);
            var exact = matches.Where(r => string.Equals(r.TargetId, targetId, StringComparison.Ordinal)).ToList();
            return exact.Count == 1 ? exact[0] : null;
        }
        catch (RecordQueryTooBroadException)
        {
            // The text is still read as a key below; a search the ledger refuses finds nothing here.
            return null;
        }
    }

    /// <summary>A JSON array of the key's parts, one per key column, in key order.</summary>
    private static PreviewKey KeyParts(string text, IReadOnlyList<string> columns)
    {
        KeyTuple key;
        try
        {
            key = KeyTuple.FromJson(text);
        }
        catch (DeliveryException ex)
        {
            return PreviewKey.Refused(PreviewKeyForms.KeyParts, $"The key reads as a JSON array but is not one of text parts ({ex.Message}); give one string per key column: {Columns(columns)}.");
        }

        return key.Values.Count == columns.Count
            ? PreviewKey.Of(key, PreviewKeyForms.KeyParts)
            : PreviewKey.Refused(
                PreviewKeyForms.KeyParts,
                string.Create(CultureInfo.InvariantCulture, $"The key names {key.Values.Count} part(s), and the flow's records are keyed by {columns.Count}: {Columns(columns)}."));
    }

    /// <summary>The key the ledger recorded for the record, which is exactly the key its row is looked up by.</summary>
    private static PreviewKey Stored(RecordState record, string how)
    {
        if (record.SourceKeyJson is not { } json)
        {
            return PreviewKey.Refused(how, $"The ledger holds {record.SourceKey} without the key its row was read by; name the record by its source key instead.");
        }

        try
        {
            return PreviewKey.Of(KeyTuple.FromJson(json), how);
        }
        catch (DeliveryException ex)
        {
            return PreviewKey.Refused(how, $"The ledger holds {record.SourceKey} with a key that does not read ({ex.Message}); name the record by its source key instead.");
        }
    }

    /// <summary>
    /// A source key as the Records page shows it (<c>system:part/part</c>), or the key's value alone. With one key column the
    /// whole text is the value. With several, the parts are separated as the product writes them: <c>a | b</c> (how a key
    /// tuple reads) is taken as written; otherwise the slashes of <c>a/b</c> (how a source key reads) are the separators,
    /// and since a part can hold a slash itself (a well database's log id may), every way of choosing them is a reading.
    /// </summary>
    private static PreviewKey SourceKeyText(string text, string system, IReadOnlyList<string> columns)
    {
        var value = text.StartsWith(system + ":", StringComparison.OrdinalIgnoreCase) ? text[(system.Length + 1)..].Trim() : text;
        if (value.Length == 0)
        {
            return PreviewKey.Refused(PreviewKeyForms.SourceKey, $"'{text}' names the source system and no key; give the record's key after it: {Columns(columns)}.");
        }

        if (columns.Count == 1)
        {
            return PreviewKey.Of(KeyTuple.Of(value), PreviewKeyForms.SourceKey);
        }

        var bars = value.Split('|').Select(p => p.Trim()).ToList();
        if (bars.Count == columns.Count && bars.All(p => p.Length > 0))
        {
            return PreviewKey.Of(new KeyTuple(bars), PreviewKeyForms.SourceKey);
        }

        var slashes = new List<int>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '/')
            {
                slashes.Add(i);
            }
        }

        var readings = new List<KeyTuple>();
        var complete = Readings(value, slashes, columns.Count - 1, 0, [], readings);
        if (!complete)
        {
            return PreviewKey.Refused(
                PreviewKeyForms.SourceKey,
                string.Create(CultureInfo.InvariantCulture, $"'{value}' splits into the flow's {columns.Count} key parts ({Columns(columns)}) in more than {MaxCandidates} ways; name the parts as a JSON array such as [\"a\", \"b\"]."));
        }

        return readings.Count > 0
            ? new PreviewKey(readings, PreviewKeyForms.SourceKey, null)
            : PreviewKey.Refused(
                PreviewKeyForms.SourceKey,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The flow's records are keyed by {columns.Count} columns ({Columns(columns)}), and '{value}' does not split into that many. Name the parts as a | b, as a/b, or as a JSON array such as [\"a\", \"b\"]."));
    }

    /// <summary>
    /// Every way of cutting <paramref name="value"/> at <paramref name="cuts"/> of the slashes into parts that are not empty,
    /// in order; false when there are more than <see cref="MaxCandidates"/>.
    /// </summary>
    private static bool Readings(string value, IReadOnlyList<int> slashes, int cuts, int from, List<int> chosen, List<KeyTuple> readings)
    {
        if (chosen.Count == cuts)
        {
            var parts = new List<string>(cuts + 1);
            var start = 0;
            foreach (var at in chosen)
            {
                parts.Add(value[start..at].Trim());
                start = at + 1;
            }

            parts.Add(value[start..].Trim());
            if (parts.All(p => p.Length > 0))
            {
                if (readings.Count == MaxCandidates)
                {
                    return false;
                }

                readings.Add(new KeyTuple(parts));
            }

            return true;
        }

        for (var i = from; i <= slashes.Count - (cuts - chosen.Count); i++)
        {
            chosen.Add(slashes[i]);
            var complete = Readings(value, slashes, cuts, i + 1, chosen, readings);
            chosen.RemoveAt(chosen.Count - 1);
            if (!complete)
            {
                return false;
            }
        }

        return true;
    }

    private static string Columns(IReadOnlyList<string> columns) => string.Join(", ", columns);
}
