using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// A node's modifiers applied to the value it read, in order (osdu/docs/reference/flow/mapping-modifiers.md), among them
// a split into parts and a replace by a table the mapping lists or one read from the cache.
internal static partial class EntryValues
{
    /// <summary>
    /// Applies the modifiers in order. False when a modifier could not read the value (a date that is not a date), which
    /// holds the record whatever the entry's required flag says: the value was there, and it was wrong. A replace reading a
    /// cached table records the rows it read among the render's cache usages.
    /// </summary>
    private static bool TryModify(
        MappingEntry entry, object? value, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out object? result)
    {
        var path = entry.Target.Text;
        result = value;
        foreach (var modifier in entry.Modifiers)
        {
            if (result is null)
            {
                return true;
            }

            var text = result is string s ? s : SourceRow.Stringify(result);
            switch (modifier.Kind)
            {
                case ModifierKind.Trim:
                    result = text?.Trim();
                    break;
                case ModifierKind.Upper:
                    result = text?.ToUpperInvariant();
                    break;
                case ModifierKind.Lower:
                    result = text?.ToLowerInvariant();
                    break;
                case ModifierKind.Split:
                    result = Split(text, modifier.Separator!, modifier.Part!.Value);
                    break;
                case ModifierKind.Replace:
                    if (!TryReplace(modifier, text, path, renderer, holds, usages, searched, out result))
                    {
                        return false;
                    }

                    break;
                case ModifierKind.Equals:
                    result = text is null ? null : string.Equals(text.Trim(), modifier.Text?.Trim(), StringComparison.OrdinalIgnoreCase);
                    break;
                case ModifierKind.Date:
                    if (!DateValues.TryRead(result, modifier.Text, out var date))
                    {
                        holds.Add(modifier.Text is null
                            ? $"{path}: '{text}' is not an ISO 8601 date or date-time, such as 2026-09-01 or 2026-09-01T10:15:30Z; give the format it is written in, such as date: dd.MM.yyyy"
                            : $"{path}: '{text}' is not a date/time in the format {modifier.Text}");
                        result = null;
                        return false;
                    }

                    result = date;
                    break;
                case ModifierKind.Number:
                    if (!NumberValues.TryRead(result, modifier.DecimalSeparator, modifier.GroupSeparator, out var number, out var numberProblem))
                    {
                        holds.Add($"{path}: {numberProblem}");
                        result = null;
                        return false;
                    }

                    result = number;
                    break;
                case ModifierKind.Id:
                    if (!TryBuildId(modifier.Id!, text, entry, root, item, renderer, holds, usages, searched, out result))
                    {
                        return false;
                    }

                    break;
                case ModifierKind.Ref:
                    if (renderer.ReferenceTemplate(entry, out var referenceProblem) is not { } reference)
                    {
                        holds.Add($"{path}: {referenceProblem}");
                        result = null;
                        return false;
                    }

                    if (!TryBuildId(reference, text, entry, root, item, renderer, holds, usages, searched, out result))
                    {
                        return false;
                    }

                    break;
                default:
                    throw new DeliveryException($"{path}: modifier '{modifier.Kind}' is not supported.");
            }
        }

        return true;
    }

    private static string? Split(string? text, string separator, int part)
    {
        if (text is null)
        {
            return null;
        }

        var parts = separator == " " ? Whitespace().Split(text.Trim()) : text.Split(separator, StringSplitOptions.None);
        if (part > parts.Length)
        {
            return null;
        }

        var value = parts[part - 1].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Replaces the trimmed value by what the table lists for it (<see cref="ReplaceTables.Find"/>): an exact key first, then
    /// the one key that matches once case is ignored. A listed value may become no value. An unlisted value becomes what
    /// <c>otherwise</c> says, by default itself; an empty value stays empty, since there is nothing to look up. False, with a
    /// hold, when several keys match only once case is ignored and give different values: taking one would write a value
    /// nobody chose. A table read from the cache is looked up the same way (<see cref="TryReplaceFromCache"/>).
    /// </summary>
    private static bool TryReplace(
        Modifier modifier, string? text, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out object? result)
    {
        result = null;
        if (text is null)
        {
            return true;
        }

        var value = text.Trim();
        if (value.Length == 0)
        {
            result = value;
            return true;
        }

        if (modifier.Table is { } cached)
        {
            return TryReplaceFromCache(modifier, cached, value, path, renderer, holds, usages, searched, out result);
        }

        var lookup = ReplaceTables.Find(ReplaceTables.Of(modifier.Replacements), ReplaceTables.KeyField, ReplaceTables.ValueField, value);
        switch (lookup.Kind)
        {
            case ReplaceLookupKind.Found:
                result = lookup.Value?.Text;
                return true;
            case ReplaceLookupKind.Ambiguous:
                var keys = string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"));
                holds.Add($"{path}: '{value}' matches the replace keys {keys} only once case is ignored, and they replace it with different values; make the incoming value exact");
                return false;
            default:
                result = Otherwise(modifier.Otherwise, value);
                return true;
        }
    }

    /// <summary>
    /// Replaces the value by what a table read from the cache gives for it: the row whose <c>match</c> field holds the
    /// value, by the cache's rules, gives what it holds at <c>field</c> (<see cref="ReplaceTables.Fields"/> settles either
    /// when the replace leaves it out). A row whose field is empty gives no value; <c>otherwise</c> decides only when no
    /// row holds the value.
    /// </summary>
    /// <remarks>
    /// Every row the replacement was decided by is recorded among the render's cache usages: the value it matched, and what
    /// it gave or that it gave nothing. A key a lookup table lists no row under is recorded too. So a later version of the
    /// table that changes a row, empties it, drops it, or comes to list a key it did not, tags exactly the records built
    /// from it. The record is held, whatever the entry's required flag says, when the cache version holds no such table,
    /// when its fields cannot be settled, when several rows answer to the value with different values, or when the row
    /// holds several values at its field: each of those leaves a value nobody chose.
    /// </remarks>
    private static bool TryReplaceFromCache(
        Modifier modifier, CachedReplaceTable table, string value, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out object? result)
    {
        result = null;
        var version = CacheLabel(renderer.Context);
        var type = renderer.References.Type(table.CacheType);
        if (type is null)
        {
            var noType = $"{path}: replace reads $cache.{table.CacheType}, and {version} holds no type '{table.CacheType}'";
            searched.Miss(new RenderMiss { Kind = RenderMissKind.NoType, Reason = noType, Types = [table.CacheType] });
            holds.Add(noType);
            return false;
        }

        if (ReplaceTables.Fields(table, type, out var problem) is not { } fields)
        {
            holds.Add($"{path}: {problem}");
            return false;
        }

        var lookup = ReplaceTables.Find(type, fields.Match, fields.Field, value);
        if (lookup.Kind == ReplaceLookupKind.Ambiguous)
        {
            var rows = string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"));
            var ambiguous =
                $"{path}: '{value}' matches the {type.Name} rows {rows} by {fields.Match} only once case is ignored in {version}, and they replace it with different values; make the incoming value exact";
            searched.Miss(new RenderMiss
            {
                Kind = RenderMissKind.Ambiguous,
                Reason = ambiguous,
                Types = [type.Name],
                Tries = [new RenderMissTry(fields.Match, null, value)],
                Candidates = lookup.Rows.Select(row => row.Id).ToList(),
            });
            holds.Add(ambiguous);
            return false;
        }

        if (lookup.Kind == ReplaceLookupKind.NotListed)
        {
            // Only a key can be told apart from every other value a row might come to hold, so it is a key a lookup table
            // does not list that is recorded; a key longer than any the table can hold never will be listed. A type of OSDU
            // records notes the value and the field it was looked for under, as a findBy line that found none does, so a
            // version holding a record by it (a code arriving as reference data) reaches the record.
            if (type.IsLookup && fields.Match.Equals(type.Key, StringComparison.OrdinalIgnoreCase) && value.Length <= LookupKeys.MaxLength)
            {
                usages.Add(new CacheUsage(type.Name, CacheUsage.ListingKey(value), fields.Field, value, CacheUsageKind.Unlisted));
            }
            else if (!type.IsLookup)
            {
                usages.Add(new CacheUsage(type.Name, CacheUsage.ListingKey(value), ReferenceField.Normalize(fields.Match), value, CacheUsageKind.Unlisted));
            }

            result = Otherwise(modifier.Otherwise, value);
            return true;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, fields.Match, value, CacheUsageKind.Match));
        }

        if (lookup.Value is not { } given || given.Node is JsonArray { Count: 0 })
        {
            foreach (var row in lookup.Rows)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, fields.Field, string.Empty, CacheUsageKind.Empty));
            }

            return true;
        }

        if (ReplaceTables.SingleText(given) is not { } replaced)
        {
            var held = given.Node is JsonArray many ? $"{many.Count} values" : "an object";
            holds.Add(
                $"{path}: {type.Name} '{lookup.Rows[0].Id}' holds {held} at '{fields.Field}' in {version} ({Clip(given.Text)}), and a replace turns '{value}' into one value; replace by a field that holds one");
            return false;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, fields.Field, given.Text, CacheUsageKind.Value));
        }

        result = replaced;
        return true;
    }

    /// <summary>What an unlisted value becomes under a replace's <c>otherwise</c>.</summary>
    private static string? Otherwise(ReplaceFallback fallback, string value) => fallback.Kind switch
    {
        ReplaceFallbackKind.Keep => value,
        ReplaceFallbackKind.Empty => null,
        _ => fallback.Text,
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
