using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// A $findAll node (osdu/docs/reference/flow/mapping-lookups-find-all.md): the field it reads from every cached row whose
// key field holds a key its operand gives and whose fields it asks to be empty hold nothing.
internal static partial class EntryValues
{
    /// <summary>
    /// The value of a <c>$findAll</c> node: the field it reads from every row of its type whose key field holds a value its
    /// operand gives and whose every field the node asks to be empty holds nothing, as a list in the order of the rows' ids,
    /// a value two rows give written once. Every key is recorded with the rows it found, none included, and so is every
    /// field a row was read or passed over for, so a later version that lists another row under a key, drops one, or
    /// changes what one gives, reaches every record built from it. A key that is an OSDU reference finds the rows that name
    /// the record with or without the version separator, since both name the same record.
    /// </summary>
    private static object? CachedAll(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var query = entry.FindAll!;
        var source = entry.Source!;
        var version = CacheLabel(renderer.Context);
        var type = renderer.References.Type(query.Type);
        var operandColumns = query.Operand.Column is { } operandColumn ? (IReadOnlyList<DatasetColumn>)[operandColumn] : [];
        if (type is null)
        {
            var noType = $"{path}: the cache holds no type '{query.Type}' in {version}";
            searched.Miss(new RenderMiss { Kind = RenderMissKind.NoType, Reason = noType, Types = [query.Type], Columns = operandColumns });
            holds.Add(noType);
            return null;
        }

        if (!TryKeys(entry, query.Operand, root, item, renderer, holds, usages, searched, out var keys))
        {
            return null;
        }

        if (keys.Count == 0)
        {
            return Missing(
                entry,
                new RenderMiss
                {
                    Kind = RenderMissKind.Empty,
                    Reason = $"{path}: {query.Operand} gives no value, so no {type.Name} row is found by {ReferenceField.Normalize(query.Field)}",
                    Columns = operandColumns,
                    Types = [type.Name],
                },
                holds,
                searched);
        }

        var field = ReferenceField.Normalize(query.Field);
        var rows = new List<ReferenceItem>();
        foreach (var key in keys.SelectMany(KeyForms).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = type.FindAll(query.Field, key);
            usages.Add(CacheUsage.Listing(type.Name, field, key, found.Select(row => row.Id)));
            foreach (var row in found)
            {
                if (!rows.Contains(row) && Passes(type, row, query, usages))
                {
                    rows.Add(row);
                }
            }
        }

        var values = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(row => row.Id, StringComparer.Ordinal))
        {
            if (source.ReadsRecordId && !type.IsLookup)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, "id", row.Id, CacheUsageKind.Value));
                Add(JsonValue.Create(WithVersionSeparator(row.Id)));
                continue;
            }

            if (type.Value(row, source.CacheField!) is not { } cached)
            {
                // Built without a value here, so a later version giving the row one changes the record.
                usages.Add(new CacheUsage(type.Name, row.Id, ReferenceField.Normalize(source.CacheField!), string.Empty, CacheUsageKind.Empty));
                continue;
            }

            usages.Add(new CacheUsage(type.Name, row.Id, ReferenceField.Normalize(source.CacheField!), cached.Text, CacheUsageKind.Value));
            foreach (var value in cached.Node is JsonArray set ? set.ToList() : [cached.Node])
            {
                if (value is not null)
                {
                    Add(value);
                }
            }
        }

        if (values.Count > 0)
        {
            return values;
        }

        var keysText = string.Join(", ", keys.Select(k => $"'{k}'"));
        var narrowing = query.Empty.Select(f => $"{ReferenceField.Normalize(f)} empty")
            .Concat(query.Contains.Select(c => $"{ReferenceField.Normalize(c.Field)} containing its {ReferenceField.Normalize(c.Part)}"))
            .ToList();
        var narrowed = narrowing.Count == 0 ? string.Empty : $" with {string.Join(" and ", narrowing)}";
        if (rows.Count > 0)
        {
            // The rows are there and hold nothing at the field read: what is missing is in the cache's own rows, not the source.
            return Missing(
                entry,
                new RenderMiss
                {
                    Kind = RenderMissKind.FieldEmpty,
                    Reason = $"{path}: the {rows.Count} {type.Name} row(s) holding {keysText} under {field} give nothing at '{source.CacheField}' in {version}",
                    Types = [type.Name],
                    Tries = [new RenderMissTry(ReferenceField.Normalize(source.CacheField!), null, string.Empty)],
                    Candidates = rows.Select(row => row.Id).ToList(),
                },
                holds,
                searched);
        }

        return Missing(
            entry,
            new RenderMiss
            {
                Kind = RenderMissKind.NoMatch,
                Reason = $"{path}: no {type.Name} row{narrowed} holds {keysText} under {field} in {version}",
                Columns = operandColumns,
                Types = [type.Name],
                EntityType = type.IsLookup ? null : type.EntityType,
                Tries = keys.Select(key => new RenderMissTry(field, query.Operand.Column, key)).ToList(),
            },
            holds,
            searched);

        void Add(JsonNode value)
        {
            var key = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text.Trim() : CanonicalJson.ToString(value);
            if (key.Length > 0 && seen.Add(key))
            {
                values.Add(value.DeepClone());
            }
        }
    }

    /// <summary>
    /// The keys a <c>$findAll</c> operand gives for the row: a literal, a column's value after the node's modifiers, or every
    /// value the path of a lookup's record holds. False when finding them held the record: a column a modifier refused, or
    /// a lookup several records answer to, which holds the record whatever the node's required flag says.
    /// </summary>
    private static bool TryKeys(
        MappingEntry entry, FindAllOperand operand, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out List<string> keys)
    {
        keys = [];
        if (operand.Literal is { } literal)
        {
            // The text reads the mapping's parameters, as every literal does; one that gives nothing is no key.
            var expanded = MappingEntry.ExpandParameters(literal, renderer.ParameterValue).Trim();
            if (expanded.Length > 0)
            {
                keys.Add(expanded);
            }

            return true;
        }

        if (operand.Column is { } column)
        {
            if (!TryModify(entry, Read(column, root, item), root, item, renderer, holds, usages, searched, out var modified))
            {
                return false;
            }

            var text = (modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified))?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                keys.Add(text);
            }

            return true;
        }

        var lookup = operand.Lookup!;
        var type = renderer.References.Type(lookup.CacheType);
        if (type is null)
        {
            var noType = $"{entry.Target.Text}: lookup {lookup.Name} reads {lookup.CacheType}, and the cache holds no type '{lookup.CacheType}' in {CacheLabel(renderer.Context)}";
            searched.Miss(new RenderMiss { Kind = RenderMissKind.NoType, Reason = noType, Types = [lookup.CacheType], Columns = lookup.FindBy.SelectMany(f => f.Comparisons).Select(f => f.Column).OfType<DatasetColumn>().Distinct().ToList() });
            holds.Add(noType);
            return false;
        }

        // The lookup is asked as the optional node it would be on its own: finding no record gives no keys, and the node
        // that reads them says what that means.
        var reader = new MappingEntry
        {
            Index = entry.Index,
            Location = entry.Location,
            Target = entry.Target,
            Source = new MappingSource { Kind = MappingSourceKind.Cache, CacheType = lookup.CacheType, CacheField = operand.LookupPath, Lookup = lookup.Name },
            FindBy = lookup.FindBy,
            Modifiers = lookup.Modifiers,
            IgnoreSeparators = lookup.IgnoreSeparators,
            Required = false,
        };
        var mistakes = new List<string>();
        var located = Locate(reader, type, root, item, renderer, mistakes, usages, searched);
        if (mistakes.Count > 0)
        {
            holds.AddRange(mistakes);
            return false;
        }

        if (located.Item is not { } record)
        {
            // An id the cache does not hold names its record by itself, and only its id can be read from it.
            if (located.Written is string id && ReferenceField.IsId(operand.LookupPath!))
            {
                keys.Add(id);
            }

            return true;
        }

        var field = ReferenceField.Normalize(operand.LookupPath!);
        if (ReferenceField.IsId(field) && type.MeansRecordId(field))
        {
            usages.Add(new CacheUsage(type.Name, record.Id, "id", record.Id, CacheUsageKind.Value));
            keys.Add(record.Id);
            return true;
        }

        if (type.Value(record, field) is not { } value || value.Terms.Count == 0)
        {
            // Built without a value here, so a later version giving the record one (a wellbore given its field) changes it.
            usages.Add(new CacheUsage(type.Name, record.Id, field, string.Empty, CacheUsageKind.Empty));
            return true;
        }

        usages.Add(new CacheUsage(type.Name, record.Id, field, value.Text, CacheUsageKind.Value));
        keys.AddRange(value.Terms);
        return true;
    }

    /// <summary>
    /// The forms a key finds rows by: the key, and for an OSDU reference the same reference with and without the separator
    /// before its version, since <c>dev:master-data--Field:1234:</c> and <c>dev:master-data--Field:1234</c> name one record.
    /// </summary>
    private static IEnumerable<string> KeyForms(string key)
    {
        yield return key;
        if (!OsduId().IsMatch(key) || CachedReferences.Parse(key) is not { } reference)
        {
            yield break;
        }

        var bare = reference.Id;
        if (!string.Equals(bare, key, StringComparison.Ordinal))
        {
            yield return bare;
        }

        var written = WithVersionSeparator(bare);
        if (!string.Equals(written, key, StringComparison.Ordinal))
        {
            yield return written;
        }
    }

    /// <summary>
    /// Whether a row a <c>$findAll</c> key found holds nothing under every field the node asks to be empty, and meets every
    /// contains line. Each field a row passed on is recorded as read empty, and the first it failed on with what it held; a
    /// contains line records both of its fields as the row held them. So a later version that fills one, empties the other,
    /// or renames a group, reaches the record.
    /// </summary>
    private static bool Passes(ReferenceType type, ReferenceItem row, FindAllQuery query, List<CacheUsage> usages)
    {
        foreach (var field in query.Empty)
        {
            var name = ReferenceField.Normalize(field);
            if (type.HoldsNothing(row, field))
            {
                usages.Add(new CacheUsage(type.Name, row.Id, name, string.Empty, CacheUsageKind.Empty));
                continue;
            }

            usages.Add(new CacheUsage(type.Name, row.Id, name, type.Value(row, field)!.Text, CacheUsageKind.Value));
            return false;
        }

        foreach (var line in query.Contains)
        {
            var text = Judged(type, row, line.Field, usages);
            var part = Judged(type, row, line.Part, usages);
            if (!text.Any(t => part.Any(p => ReferenceKeyFold.Holds(t, p))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The values a row holds under a field a contains line reads, recorded as read: its terms, or none when it holds nothing.</summary>
    private static IReadOnlyList<string> Judged(ReferenceType type, ReferenceItem row, string field, List<CacheUsage> usages)
    {
        var name = ReferenceField.Normalize(field);
        if (type.Value(row, field) is not { Terms.Count: > 0 } value)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, name, string.Empty, CacheUsageKind.Empty));
            return [];
        }

        usages.Add(new CacheUsage(type.Name, row.Id, name, value.Text, CacheUsageKind.Value));
        return value.Terms;
    }
}
