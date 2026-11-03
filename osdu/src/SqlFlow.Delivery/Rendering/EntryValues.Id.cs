using System.Text;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

internal static partial class EntryValues
{
    /// <summary>
    /// Builds the id an id modifier's template describes from the value the modifiers before it gave: each token's value,
    /// trimmed and percent-encoded (<see cref="IdValues.Encode"/>), in its place, then the whole checked against OSDU's id
    /// shape and what the template gives the variable (<see cref="IdValues.Problem"/>). A value that already is an OSDU id
    /// names its record and is written as it is, when the template reads it and builds ids of its entity type. Where the
    /// cache version the render reads holds records of the entity type the id names, the id has to name one of them
    /// (<see cref="Resolves"/>).
    /// </summary>
    /// <remarks>
    /// A token with no value leaves no id to write: a required entry holds the record, naming the token, and an optional one
    /// leaves the variable out; a half-built id is never written. An id naming a record the cache does not hold is treated
    /// the same way, as a cache miss is. A parameter the flow gives no value, a cached lookup the
    /// cache version cannot answer for certain, text that is not valid Unicode and an id the check refuses hold the record
    /// whatever the required flag says: each is a mistake in the mapping or its data, not a value that is simply absent.
    /// </remarks>
    private static bool TryBuildId(
        IdTemplate template, string? text, MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages,
        RenderTrail searched,
        out object? result)
    {
        result = null;
        var path = entry.Target.Text;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        string id;
        if (template.ReadsValue && OsduId().IsMatch(value))
        {
            var named = value.Split(':')[1];
            if (template.EntityType is { } built && !string.Equals(built, named, StringComparison.Ordinal))
            {
                holds.Add($"{path}: '{value}' is already an OSDU id, of a {named} record, and the id modifier builds {built} ids ({template})");
                return false;
            }

            id = WithVersionSeparator(value);
        }
        else
        {
            var written = new StringBuilder();
            var missing = new List<string>();
            foreach (var token in template.Tokens)
            {
                string? part;
                switch (token.Kind)
                {
                    case IdTokenKind.Text:
                        written.Append(token.Text);
                        continue;
                    case IdTokenKind.Value:
                        part = value;
                        break;
                    case IdTokenKind.Dataset:
                        part = SourceRow.Stringify(Read(token.Column!, root, item))?.Trim();
                        break;
                    case IdTokenKind.Parameter:
                        part = renderer.ParameterValue(token.Parameter!)?.Trim();
                        if (string.IsNullOrEmpty(part))
                        {
                            holds.Add($"{path}: the id {template} reads {token}, which the flow supplies no value for");
                            return false;
                        }

                        break;
                    default:
                        if (!TryIdCacheValue(token, value, path, renderer, holds, usages, searched, out part))
                        {
                            return false;
                        }

                        break;
                }

                if (string.IsNullOrEmpty(part))
                {
                    missing.Add(token.Text);
                    continue;
                }

                if (IdValues.Encode(part) is not { } encoded)
                {
                    holds.Add($"{path}: {token} holds text that is not valid Unicode, which no id can carry");
                    return false;
                }

                written.Append(encoded);
            }

            if (missing.Count > 0)
            {
                // A token of the row that is empty leaves nothing to build: the columns behind those tokens are what is empty.
                var emptyColumns = searched.NotesMisses
                    ? template.Tokens.Where(t => t.Kind == IdTokenKind.Dataset && missing.Contains(t.Text)).Select(t => t.Column!).Distinct().ToList()
                    : [];
                if (emptyColumns.Count > 0)
                {
                    searched.Miss(new RenderMiss
                    {
                        Kind = RenderMissKind.Empty,
                        Reason = $"{path}: the id {template} cannot be built, since {string.Join(", ", missing.Distinct(StringComparer.Ordinal))} has no value",
                        Columns = emptyColumns,
                    });
                }

                if (entry.Required)
                {
                    var tokens = missing.Distinct(StringComparer.Ordinal).ToList();
                    holds.Add($"{path}: the id {template} cannot be built, since {string.Join(", ", tokens)} {(tokens.Count == 1 ? "has" : "have")} no value, and the entry is required");
                    return false;
                }

                return true;
            }

            id = written.ToString();
        }

        if (IdValues.Problem(id, renderer.Schema.Resolve(entry.Target.SchemaPath)) is { } problem)
        {
            holds.Add($"{path}: the id {template} gives an id that cannot be written: {problem}");
            return false;
        }

        if (!Resolves(id, template, entry, renderer, usages, out var unheld))
        {
            // A reference to a record the partition does not hold would reach OSDU pointing at nothing, so it is never
            // written: a required entry holds the record with the reason, and an optional one leaves the variable out.
            searched.Miss(unheld!);
            if (entry.Required)
            {
                holds.Add(unheld!.Reason);
                return false;
            }

            return true;
        }

        result = id;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="id"/> names a record the cache version the render reads holds, where that version holds
    /// records of the entity type the id names at all: the reference data a cache flow captured from the partition, which
    /// is what the partition holds. A version holding none of that entity type answers nothing, and the id is written as it
    /// was built. A record found is recorded among the render's dependencies, so a later version that drops or changes it
    /// reaches this record. False, with the reason, when the version holds records of the entity type and not this one,
    /// unless the node lets an unverified id go out (<see cref="MappingEntry.Unverified"/>): then the id is written and
    /// recorded as unverified.
    /// </summary>
    /// <remarks>
    /// The record is looked up by its id, exactly: OSDU ids that differ only by case are different records, and the id a
    /// template builds is the one written, so the lookup asks for no other. An id whose code itself carries colons and that
    /// ends in a version cannot be told apart from its version by its text, so it is not looked up.
    /// </remarks>
    private static bool Resolves(string id, IdTemplate template, MappingEntry entry, MappingRenderer renderer, List<CacheUsage> usages, out RenderMiss? missing)
    {
        missing = null;
        if (CachedReferences.Named(id) is not { } reference)
        {
            return true;
        }

        var holding = CachedReferences.Holding(renderer.References, reference.EntityType);
        if (holding.Count == 0)
        {
            return true;
        }

        if (CachedReferences.Find(holding, reference) is { } found)
        {
            usages.Add(new CacheUsage(found.Type.Name, found.Item.Id, "id", found.Item.Id, CacheUsageKind.Match));
            return true;
        }

        if (entry.Unverified)
        {
            // The node lets the id go out without its record ($unverified): the render notes, in every type that would hold
            // the record, the id it wrote without finding it there, so a capture that brings the record in reaches this
            // record, and the ledger shows the reference was unverified when it was written.
            foreach (var type in holding)
            {
                usages.Add(new CacheUsage(type.Name, reference.Id, "id", id, CacheUsageKind.Unverified));
            }

            return true;
        }

        // The record is held. The render notes the id it looked for in every type that would hold the record, so a version that
        // brings the record in, by a refresh or a pipeline's cache step, plans the held record again.
        foreach (var type in holding)
        {
            usages.Add(new CacheUsage(type.Name, reference.Id, "id", id, CacheUsageKind.Missing));
        }

        missing = new RenderMiss
        {
            Kind = RenderMissKind.IdNotHeld,
            Reason = $"{entry.Target.Text}: the id {template} gives {id}, and {CacheLabel(renderer.Context)} holds no such {reference.EntityType} record in {string.Join(", ", holding.Select(t => t.Name))}, so the reference would point at nothing",
            Columns = IdColumns(template, entry),
            Types = holding.Select(t => t.Name).ToList(),
            EntityType = reference.EntityType,
            Tries = [new RenderMissTry("id", entry.Source?.Column, reference.Id)],
        };
        return false;
    }

    /// <summary>
    /// The source columns an id is built from: the column the node reads, whose value the template's value token takes, and the
    /// columns its dataset tokens read.
    /// </summary>
    private static IReadOnlyList<DatasetColumn> IdColumns(IdTemplate template, MappingEntry entry)
    {
        var columns = new List<DatasetColumn>();
        if (template.ReadsValue && entry.Source?.Column is { } read)
        {
            columns.Add(read);
        }

        columns.AddRange(template.Columns.Where(column => !columns.Contains(column)));
        return columns;
    }

    /// <summary>
    /// What a cache token gives for the entry's value: the field it names, of the row of the lookup table whose key is the
    /// value, matched as a replace matches it (<see cref="ReplaceTables.Find"/>). Null, with true, when no row is listed
    /// under the value or the row holds nothing there; every row read and every key not listed is recorded among the
    /// render's cache usages, so a later version of the table tags exactly the records it changes.
    /// </summary>
    private static bool TryIdCacheValue(
        IdToken token, string value, string path, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out string? part)
    {
        part = null;
        var version = CacheLabel(renderer.Context);
        if (renderer.References.Type(token.CacheType!) is not { } type)
        {
            var noType = $"{path}: the id reads {token}, and {version} holds no type '{token.CacheType}'";
            searched.Miss(new RenderMiss { Kind = RenderMissKind.NoType, Reason = noType, Types = [token.CacheType!] });
            holds.Add(noType);
            return false;
        }

        if (type.Key is not { } key)
        {
            holds.Add(
                $"{path}: the id reads {token}, and {type.Name} holds OSDU records ({type.EntityType}) in {version}, which have no key to look the value up by; a cache token reads a lookup table");
            return false;
        }

        var field = ReferenceField.Normalize(token.CacheField!);
        var lookup = ReplaceTables.Find(type, key, field, value);
        switch (lookup.Kind)
        {
            case ReplaceLookupKind.Ambiguous:
                holds.Add(
                    $"{path}: '{value}' matches the {type.Name} rows {string.Join(", ", lookup.Rows.Select(row => $"'{row.Id}'"))} only once case is ignored in {version}, and they hold different values at '{field}'; make the incoming value exact");
                return false;
            case ReplaceLookupKind.NotListed:
                if (value.Length <= LookupKeys.MaxLength)
                {
                    usages.Add(new CacheUsage(type.Name, CacheUsage.ListingKey(value), field, value, CacheUsageKind.Unlisted));
                }

                return true;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, ReferenceField.Normalize(key), value, CacheUsageKind.Match));
        }

        if (lookup.Value is not { } given || given.Node is System.Text.Json.Nodes.JsonArray { Count: 0 })
        {
            foreach (var row in lookup.Rows)
            {
                usages.Add(new CacheUsage(type.Name, row.Id, field, string.Empty, CacheUsageKind.Empty));
            }

            return true;
        }

        if (ReplaceTables.SingleText(given) is not { } single)
        {
            holds.Add(
                $"{path}: {type.Name} '{lookup.Rows[0].Id}' holds several values or an object at '{field}' in {version} ({Clip(given.Text)}), and {token} writes one value into the id");
            return false;
        }

        foreach (var row in lookup.Rows)
        {
            usages.Add(new CacheUsage(type.Name, row.Id, field, given.Text, CacheUsageKind.Value));
        }

        part = single.Trim();
        return true;
    }
}
