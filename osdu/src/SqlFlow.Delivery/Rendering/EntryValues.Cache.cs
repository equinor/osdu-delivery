using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// A cache source (osdu/docs/reference/flow/mapping-lookups.md, $cache and $findBy): the record its findBy lines find in
// the version of the partition's cache the render reads, and what the node writes of it, a cached field or the
// reference a relationship takes, each recorded among the render's dependencies.
internal static partial class EntryValues
{
    /// <summary>
    /// Resolves a cache source: each findBy line in turn compares its cached field with its value, and the first line that
    /// finds exactly one record wins. The value an entry matched by and the value it read are both recorded as
    /// dependencies of the render, so a later cache version can tell which records it changes.
    /// </summary>
    private static object? Cached(MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var typeName = entry.Source!.CacheType!;
        var type = renderer.References.Type(typeName);
        if (type is null)
        {
            var reason = $"{path}: the cache holds no type '{typeName}' in {CacheLabel(renderer.Context)}";
            searched.Miss(new RenderMiss { Kind = RenderMissKind.NoType, Reason = reason, Types = [typeName], Columns = FindColumns(entry) });
            holds.Add(reason);
            return null;
        }

        var located = Locate(entry, type, root, item, renderer, holds, usages, searched);
        return located.Item is { } hit ? Select(entry, type, hit, renderer, holds, usages, searched) : located.Written;
    }

    /// <summary>
    /// What a cache node's findBy lines find: the one record the first line that finds one names, or an id the cache does
    /// not hold that the node writes as it is (<see cref="Located.Written"/>), or nothing, with the reason held or left out
    /// as the node's required flag says. Several records answering to a value hold the record whatever the flag says. A line
    /// of several comparisons (<c>Well = well_name and Mnemonic = curve_mnemonic</c>) finds the record that meets every one
    /// of them, by the tiers a single value is matched by, and is skipped when any of its values is empty, as a line of one
    /// is when its value is.
    /// </summary>
    private static Located Locate(
        MappingEntry entry, ReferenceType type, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var source = entry.Source!;
        var typeName = type.Name;
        var version = CacheLabel(renderer.Context);
        var tried = new List<IReadOnlyList<(FindBy Find, string Value)>>();
        var emptied = new List<EmptiedValue>();
        var ambiguous = new List<(IReadOnlyList<(FindBy Find, string Value)> Line, ReferenceMatch Found)>();
        foreach (var line in entry.FindBy)
        {
            if (!TryLineValues(entry, line, type, root, item, renderer, holds, usages, searched, emptied, out var values))
            {
                return Located.Nothing;
            }

            if (values is null)
            {
                // A value the line compares is empty, so the line cannot decide; a later line may.
                continue;
            }

            tried.Add(values);
            if (values.Count > 1)
            {
                var met = type.Find(values.Select(v => (v.Find.Field, v.Value)).ToList(), entry.IgnoreSeparators);
                if (met.Item is { } row)
                {
                    NoteEarlierLines();

                    // The record is found by every value together, so each is what it still has to hold: a later version in
                    // which it no longer holds any one of them stops it being the record the line finds.
                    foreach (var (find, value) in values)
                    {
                        usages.Add(new CacheUsage(typeName, row.Id, ReferenceField.Normalize(find.Field), value, CacheUsageKind.Match));
                    }

                    return new Located(row, null);
                }

                if (met.IsCaseAmbiguous)
                {
                    ambiguous.Add((values, met));
                }

                continue;
            }

            var (one, oneValue) = values[0];

            // A value that already is an OSDU id names its record without a lookup, found by its record id whatever the type
            // caches under a field called ID. A lookup table holds no OSDU records, so there a value of that shape is matched
            // like any other.
            if (!type.IsLookup && OsduId().IsMatch(oneValue))
            {
                if (RecordIds(oneValue).Select(type.ById).FirstOrDefault(found => found is not null) is { } byId)
                {
                    usages.Add(new CacheUsage(typeName, byId.Id, "id", oneValue, CacheUsageKind.Match));
                    return new Located(byId, null);
                }

                if (source.ReadsRecordId)
                {
                    return new Located(null, AsReference(oneValue));
                }

                // The id the record was built without is noted, so a version that brings the record in reaches it: a held record
                // is planned again (osdu/docs/reference/concepts/partition-cache.md, A record held for a reference waits for
                // it), and one delivered without the property is tagged.
                usages.Add(new CacheUsage(typeName, RecordIds(oneValue).First(), "id", oneValue, CacheUsageKind.Missing));

                Missing(
                    entry,
                    new RenderMiss
                    {
                        Kind = RenderMissKind.IdNotHeld,
                        Reason = $"{path}: '{oneValue}' is already an OSDU id that {version} does not hold, so '{source.CacheField}' cannot be read from the cache",
                        Columns = one.Column is { } idColumn ? [idColumn] : [],
                        Types = [typeName],
                        EntityType = CachedReferences.Parse(oneValue)?.EntityType,
                        Tries = [new RenderMissTry("id", one.Column, oneValue)],
                    },
                    holds,
                    searched);
                return Located.Nothing;
            }

            var found = type.Find(one.Field, oneValue, entry.IgnoreSeparators);
            if (found.Item is { } hit)
            {
                NoteEarlierLines();
                usages.Add(new CacheUsage(typeName, hit.Id, ReferenceField.Normalize(one.Field), oneValue, CacheUsageKind.Match));
                return new Located(hit, null);
            }

            if (found.IsCaseAmbiguous)
            {
                ambiguous.Add((values, found));
            }
        }

        if (ambiguous.Count > 0)
        {
            // Several records answer to the value. Taking the first would deliver the wrong reference without a trace, so
            // the record is held whatever the required flag says, and the reason names every candidate.
            var choice = ambiguous[0];
            var candidates = string.Join(", ", choice.Found.CaseVariants.Select(c => c.Id));
            var count = choice.Found.CaseVariants.Count;
            var several = choice.Line.Count == 1
                ? $"{path}: '{choice.Line[0].Value}' matches {count} {typeName} records by {ReferenceField.Normalize(choice.Line[0].Find.Field)} {choice.Found.Loosening} ({candidates}) in {version}; make the incoming value exact with a replace modifier"
                : $"{path}: {Compared(choice.Line)} match {count} {typeName} records {choice.Found.Loosening} ({candidates}) in {version}; make the incoming values exact with a replace modifier, or compare another field that tells them apart";
            searched.Miss(new RenderMiss
            {
                Kind = RenderMissKind.Ambiguous,
                Reason = several,
                Columns = choice.Line.Select(t => t.Find.Column).OfType<DatasetColumn>().Distinct().ToList(),
                Types = [typeName],
                EntityType = type.IsLookup ? null : type.EntityType,
                Tries = MissTries(choice.Line).ToList(),
                Candidates = choice.Found.CaseVariants.Select(c => c.Id).ToList(),
                IgnoreSeparators = entry.IgnoreSeparators,
            });
            holds.Add(several);
            return Located.Nothing;
        }

        if (tried.Count == 0)
        {
            if (entry.Required || searched.NotesMisses)
            {
                var nothing = $"{path}: {EmptiedText(emptied, entry)}, so there is nothing to find in the cache";
                var blank = emptied.Where(e => e.Held is null).Select(e => e.Column).Distinct().ToList();
                if (blank.Count > 0)
                {
                    searched.Miss(new RenderMiss { Kind = RenderMissKind.Empty, Reason = nothing, Columns = blank, Types = [typeName] });
                }

                if (entry.Required)
                {
                    holds.Add($"{nothing}, and the entry is required");
                }
            }

            return Located.Nothing;
        }

        NoteMisses(tried);

        var fold = entry.IgnoreSeparators ? ", even with punctuation and spacing ignored" : string.Empty;
        var described = tried.All(values => values.Count == 1) && tried.Select(values => values[0].Value).Distinct(StringComparer.Ordinal).Count() == 1
            ? $"'{tried[0][0].Value}' by {string.Join("/", tried.Select(values => ReferenceField.Normalize(values[0].Find.Field)))}"
            : string.Join(" or ", tried.Select(Compared));
        Missing(
            entry,
            new RenderMiss
            {
                Kind = RenderMissKind.NoMatch,
                Reason = $"{path}: no {typeName} matches {described}{fold} in {version}",
                Columns = tried.SelectMany(values => values).Select(t => t.Find.Column).OfType<DatasetColumn>().Distinct().ToList(),
                Types = [typeName],
                EntityType = type.IsLookup ? null : type.EntityType,
                Tries = tried.SelectMany(MissTries).ToList(),
                IgnoreSeparators = entry.IgnoreSeparators,
            },
            holds,
            searched);
        return Located.Nothing;

        // The lines tried before the one that found the record decide nothing now, and would in a version where they find one:
        // the first line that finds exactly one record wins. So each is noted as a render that found nothing notes it, and a
        // line several records answered to notes each of them, so a version leaving it one record reaches this one.
        void NoteEarlierLines()
        {
            foreach (var (line, found) in ambiguous)
            {
                foreach (var candidate in found.CaseVariants)
                {
                    foreach (var (find, value) in line)
                    {
                        usages.Add(new CacheUsage(typeName, candidate.Id, ReferenceField.Normalize(find.Field), value, CacheUsageKind.Match));
                    }
                }
            }

            NoteMisses(tried.Take(tried.Count - 1).Where(line => !ambiguous.Any(a => ReferenceEquals(a.Line, line))));
        }

        void NoteMisses(IEnumerable<IReadOnlyList<(FindBy Find, string Value)>> lines)
        {
            foreach (var values in lines)
            {
                if (values.Count > 1)
                {
                    // The values no row met together are noted together, of a lookup table as of a type of OSDU records, so a
                    // later version holding a row that meets them all reaches the record, and one holding a row that shares
                    // only some of them does not.
                    if (CacheUsage.CompositeMiss(typeName, values.Select(v => (v.Find.Field, v.Value)).ToList(), entry.IgnoreSeparators) is { } miss)
                    {
                        usages.Add(miss);
                    }
                }
                else if (entry.IgnoreSeparators)
                {
                    // A value the node folds punctuation and spacing away for may be found by a record holding it written
                    // another way (WB_A_12 for WB A-12), which no key of the value tells; it is noted as a composite miss,
                    // which every version of the type is asked again, of a lookup table as of a type of OSDU records.
                    if (CacheUsage.CompositeMiss(typeName, [(values[0].Find.Field, values[0].Value)], ignoreSeparators: true) is { } folded)
                    {
                        usages.Add(folded);
                    }
                }
                else if (!type.IsLookup)
                {
                    // Each value no record answered to is recorded, so a later version holding a record it finds (a wellbore
                    // loaded after the logs that name it) reaches the records built without one.
                    var (find, value) = values[0];
                    usages.Add(new CacheUsage(typeName, CacheUsage.ListingKey(value), ReferenceField.Normalize(find.Field), value, CacheUsageKind.Unlisted));
                }
            }
        }
    }

    /// <summary>The values one findBy line compared, as a miss keeps them: each comparison after the first marked as joining it.</summary>
    private static IEnumerable<RenderMissTry> MissTries(IReadOnlyList<(FindBy Find, string Value)> values)
        => values.Select((t, i) => new RenderMissTry(ReferenceField.Normalize(t.Find.Field), t.Find.Column, t.Value, And: i > 0));

    /// <summary>The values one findBy line compared, as a reason states them: <c>Well 'A-12' and Mnemonic 'GR'</c>.</summary>
    private static string Compared(IReadOnlyList<(FindBy Find, string Value)> values)
        => string.Join(" and ", values.Select(t => $"{ReferenceField.Normalize(t.Find.Field)} '{t.Value}'"));

    /// <summary>
    /// What a cache node's lines found: the record they name, or an id the node writes without a record the cache holds,
    /// or neither.
    /// </summary>
    private readonly record struct Located(ReferenceItem? Item, object? Written)
    {
        public static Located Nothing => default;
    }

    /// <summary>The cache version a render read, as a hold reason names it: "version 20260910T165153Z of the cache of partition 'dev'".</summary>
    private static string CacheLabel(RenderContext context)
        => context.CacheScope is null ? $"cache version {context.CacheVersion}" : $"version {context.CacheVersion} of the cache of partition '{context.CacheScope}'";

    private static object? Select(MappingEntry entry, ReferenceType type, ReferenceItem hit, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var source = entry.Source!;
        if (source.ReadsRecordId && type.IsLookup)
        {
            // A lookup row's id is its key, not an OSDU record id, and writing it as a reference would point at nothing. The
            // gate refuses this mapping; a render that meets it anyway holds, whatever the entry's required flag says.
            holds.Add(
                $"{entry.Target.Text}: {type.Name} is a lookup table in {CacheLabel(renderer.Context)}, whose rows are not OSDU records, so it has no id to write; read one of its fields ({string.Join(", ", type.FieldNames)})");
            return null;
        }

        if (source.ReadsRecordId)
        {
            usages.Add(new CacheUsage(type.Name, hit.Id, "id", hit.Id, CacheUsageKind.Value));
            return WithVersionSeparator(hit.Id);
        }

        var field = source.CacheField!;
        if (type.Value(hit, field) is not { Terms.Count: > 0 } cached)
        {
            // The document is built without the value, so a later version giving the record one changes it. Blank text, an
            // empty list or an object of empty values is no value either, as it is to every other reader of the cache.
            usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), string.Empty, CacheUsageKind.Empty));
            return Missing(
                entry,
                new RenderMiss
                {
                    Kind = RenderMissKind.FieldEmpty,
                    Reason = $"{entry.Target.Text}: {type.Name} '{hit.Id}' caches nothing at '{field}' in {CacheLabel(renderer.Context)}. Cached: {string.Join(", ", type.FieldNames.Prepend("id"))}",
                    Types = [type.Name],
                    Tries = [new RenderMissTry(ReferenceField.Normalize(field), null, string.Empty)],
                    Candidates = [hit.Id],
                },
                holds,
                searched);
        }

        usages.Add(new CacheUsage(type.Name, hit.Id, ReferenceField.Normalize(field), cached.Text, CacheUsageKind.Value));
        if (TakesReference(renderer, entry))
        {
            return References(entry, type, hit, field, cached, renderer, holds, usages, searched);
        }

        return cached.Node.DeepClone();
    }

    /// <summary>Whether the entry's variable is a relationship, or a list of them: what it takes is an OSDU record id.</summary>
    private static bool TakesReference(MappingRenderer renderer, MappingEntry entry)
        => renderer.Schema.Resolve(entry.Target.SchemaPath) is { } property
            && (property.IsRelationship || property.Items?["x-osdu-relationship"] is JsonArray);

    /// <summary>
    /// A cached field written to a relationship: OSDU's own translations cache the id of the record a value stands for
    /// (<c>ExternalUnitOfMeasure.UnitOfMeasureID</c>), and the mapping writes it as the reference, with the colon that
    /// separates a version added where the cached id leaves it out. Where the cache holds records of the entity type an
    /// id names, the record it names is one of them, and it is recorded among the render's dependencies, so a version
    /// that drops it reaches this record.
    /// </summary>
    /// <remarks>
    /// A value that is not an OSDU record id, or one naming a record the cache holds that type of and not that record,
    /// holds the record whatever the entry's required flag says: writing it would put a reference to nothing into OSDU.
    /// The gate refuses a mapping whose cached field holds such values; this holds a render that meets one anyway.
    /// </remarks>
    private static object? References(
        MappingEntry entry, ReferenceType type, ReferenceItem hit, string field, ReferenceValue cached, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var version = CacheLabel(renderer.Context);
        var written = new List<JsonNode?>();
        foreach (var node in cached.Node is JsonArray set ? set.ToList() : [cached.Node])
        {
            var text = node is JsonValue value && value.TryGetValue<string>(out var held) ? held.Trim() : null;
            if (CachedReferences.Parse(text) is not { } reference)
            {
                holds.Add(
                    $"{path}: {type.Name} '{hit.Id}' holds {Clip(node?.ToJsonString() ?? "null")} at '{field}' in {version}, which is not an OSDU record id, and {path} takes a reference to a record");
                return null;
            }

            var holding = CachedReferences.Holding(renderer.References, reference.EntityType);
            if (holding.Count > 0)
            {
                if (CachedReferences.Find(holding, reference) is not { } found)
                {
                    // The record is held. The render notes the id it looked for in every type that would hold the record, so a
                    // version that brings the record in plans the held record again, as it does for an id a modifier built.
                    foreach (var holder in holding)
                    {
                        usages.Add(new CacheUsage(holder.Name, reference.Id, "id", CachedReferences.Written(text!), CacheUsageKind.Missing));
                    }

                    var unheld =
                        $"{path}: {type.Name} '{hit.Id}' names {reference.Id} at '{field}', and {version} holds no such {reference.EntityType} record in {string.Join(", ", holding.Select(t => t.Name))}, so the reference would point at nothing";
                    searched.Miss(new RenderMiss
                    {
                        Kind = RenderMissKind.IdNotHeld,
                        Reason = unheld,
                        Types = holding.Select(t => t.Name).ToList(),
                        EntityType = reference.EntityType,
                        Tries = [new RenderMissTry("id", null, reference.Id)],
                        Candidates = [hit.Id],
                    });
                    holds.Add(unheld);
                    return null;
                }

                usages.Add(new CacheUsage(found.Type.Name, found.Item.Id, "id", found.Item.Id, CacheUsageKind.Match));
            }

            written.Add(JsonValue.Create(CachedReferences.Written(text!)));
        }

        return cached.Node is JsonArray ? new JsonArray(written.ToArray()) : written[0];
    }

    /// <summary>
    /// The record ids an OSDU id written in a value may name, in the order they are tried: as a relationship parses it (a
    /// version after the code dropped), as a code holding colons of its own reads it
    /// (<c>dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031:</c>), and as written, without the separator a
    /// reference to the latest version ends in. Never empty for a value <see cref="OsduId"/> matches.
    /// </summary>
    private static IEnumerable<string> RecordIds(string id)
    {
        var trimmed = id.Trim();
        return new[] { CachedReferences.Parse(trimmed)?.Id, CachedReferences.Named(trimmed)?.Id, trimmed.TrimEnd(':') }
            .OfType<string>()
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.Ordinal);
    }
}
