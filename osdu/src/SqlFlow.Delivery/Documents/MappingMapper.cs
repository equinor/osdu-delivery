using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a mapping document (osdu/docs/reference/flow/mapping.md) into a <see cref="MappingDefinition"/>. Everything
/// that can be checked without the template is checked here, each error naming the file and where in it: the header,
/// every node of the record tree with its value, findBy lines, modifiers and condition, the forEach arrays, and the
/// envelope lists.
/// </summary>
internal static partial class MappingMapper
{
    /// <summary>The access list and legal block targets every mapping gives as literal lists, and where the record writes them.</summary>
    private static readonly (string Target, string Name, string Location)[] EnvelopeTargets =
    [
        ("osdu.acl.owners", "owners", "record.acl.owners"),
        ("osdu.acl.viewers", "viewers", "record.acl.viewers"),
        ("osdu.legal.legaltags", "legal tags", "record.legal.legaltags"),
        ("osdu.legal.otherRelevantDataCountries", "countries", "record.legal.otherRelevantDataCountries"),
    ];

    /// <summary>
    /// The envelope lists a mapping may add values to with nodes, after the literal values every record carries: the access
    /// lists, which a record's own data can widen (the groups of the field a wellbore lies in).
    /// </summary>
    private static readonly string[] AccessTargets = ["osdu.acl.owners", "osdu.acl.viewers"];

    /// <summary>
    /// The mapping <paramref name="y"/> describes. Its template, and the schema each search pins, name a kind and, when they
    /// pin one, a version; <paramref name="pins"/> settles each against the template folders the document reads
    /// (osdu/docs/reference/concepts/templates.md, Template folders). Read without pins, a mapping has to name its versions,
    /// which are then taken as written.
    /// </summary>
    public static MappingDefinition Map(MappingYaml y, string source, ITemplatePins? pins = null)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var version = FlowMapper.Require(y.Version, "version", source);
        var template = y.Template ?? throw FlowMapper.Missing("template", source);
        var kind = FlowMapper.Require(template.Kind, "template.kind", source);
        if (!FlowMapper.IsRecordKind(kind))
        {
            throw new FlowValidationException(
                $"{source}: template.kind '{kind}' must be 'authority:source:entityType:major.minor.patch', each of the first three made of letters, digits, underscore, hyphen and dot, as the storage service requires of every record it accepts.");
        }

        var templateVersion = string.IsNullOrWhiteSpace(template.Version) ? null : template.Version.Trim();
        if (templateVersion is not null && !TemplateVersionPattern().IsMatch(templateVersion))
        {
            throw new FlowValidationException(
                $"{source}: template.version '{templateVersion}' is not a template version. A version is the 16 hexadecimal characters the Templates page shows for a template file, such as 26a3c3441882db4f; leave it out to fill the one version of the kind the nearest template folder holds.");
        }

        var pinned = Pinned(kind, templateVersion, $"{source}: template", pins);

        if (y.Fragment is not null)
        {
            throw new FlowValidationException(
                $"{source}: fragment: is written enrichment: true: an enrichment mapping is what an enrichment of another mapping (its enrich: block) renders for each of that mapping's records (osdu/docs/reference/flow/enrich.md).");
        }

        // An enrichment mapping renders into, or beside, a record another mapping keys (osdu/docs/reference/flow/enrich.md): it
        // keys nothing, mints no id and carries no envelope of its own.
        var fragment = y.Enrichment ?? false;
        if (fragment && DspdmKinds.Is(kind))
        {
            throw new FlowValidationException(
                $"{source}: enrichment: true renders part of an OSDU record, and {kind} is a row of a DSPDM business object, which no enrichment extends; map every attribute in the row's own mapping.");
        }

        if (fragment && y.Dataset is not null)
        {
            throw new FlowValidationException(
                $"{source}: an enrichment mapping (enrichment: true) renders part of the records another mapping keys, so it declares no dataset; its rows are its enrichment's, the child dataset of the enrichment's name the flow binds under source.datasets. Remove dataset.");
        }

        if (fragment && y.Enrich is not null)
        {
            throw new FlowValidationException(
                $"{source}: an enrichment mapping (enrichment: true) declares no enrichments of its own; declare every enrichment of the record in the enrich: block of the mapping that keys it, in the order they render.");
        }

        if (y.Enrich is not null && DspdmKinds.Is(kind))
        {
            throw new FlowValidationException(
                $"{source}: enrich: adds to OSDU records, and {kind} is a row of a DSPDM business object, which the dspdm route writes whole; map every attribute in the row's own mapping.");
        }

        var dataset = fragment ? MappingDataset.None : Dataset(y.Dataset ?? throw FlowMapper.Missing("dataset", source), source);

        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new MappingParameter { Required = kv.Value?.Required ?? false, Default = kv.Value?.Default, Description = kv.Value?.Description },
            StringComparer.Ordinal);
        if (!fragment && !parameters.ContainsKey(Snapshots.RenderContext.DataPartitionParameter))
        {
            throw new FlowValidationException(
                $"{source}: the mapping must declare the '{Snapshots.RenderContext.DataPartitionParameter}' parameter; record ids and references are minted in that partition.");
        }

        // What each search: <name> node searches. Declared once here rather than on every node, so two nodes resolving
        // against the same set cannot disagree about the kind they search or the schema it is read by.
        var searches = (y.Searches ?? []).ToDictionary(
            kv => kv.Key,
            kv => ParseSearch(kv.Key, kv.Value, source, pins),
            StringComparer.Ordinal);

        var lookups = ReadLookups(y.Lookups, source);
        var entries = ReadRecord(y.Record, lookups, source);
        Validate(entries, parameters, source);
        ValidateSearches(entries, searches, source);
        ValidateLookups(entries, lookups, source);
        if (fragment)
        {
            ValidateFragment(entries, source);
        }
        else
        {
            RefuseBase(entries, source);
        }

        var enrich = MapEnrich(y.Enrich, source);
        ValidateLinks(entries, enrich, fragment, source);

        return new MappingDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Version = version,
            Template = pinned,
            TemplateVersionNamed = templateVersion is not null,
            Description = y.Description,
            Fragment = fragment,
            Enrich = enrich,
            Dataset = dataset,
            Parameters = parameters,
            Searches = searches,
            Lookups = lookups,
            Entries = entries,
            Envelope = fragment ? NoEnvelope : Envelope(entries, kind, source),
        };
    }

    /// <summary>The envelope of a fragment, which writes none: the record it adds to carries the flow's own mapping's.</summary>
    private static readonly MappingEnvelope NoEnvelope = new([], [], [], []);

    /// <summary>The record roots a fragment writes under: the record's data and its tags. Its id, kind, access, legal block and lineage are the flow's own mapping's.</summary>
    internal static readonly IReadOnlyList<string> FragmentRoots = ["data", "tags"];

    /// <summary>
    /// What identifies a record of the incoming dataset (<c>dataset</c>): its system, the key columns, the label and the
    /// identity columns, and what the OSDU id's unique segment is made from.
    /// </summary>
    private static MappingDataset Dataset(MappingDatasetYaml dataset, string source)
    {
        var declaredKey = dataset.Key is { Count: > 0 } k
            ? k
            : throw new FlowValidationException($"{source}: dataset.key must name at least one column, such as key: [log_id].");
        var key = declaredKey.Select((column, i) => KeyColumn(column, $"dataset.key[{i}]", source)).ToList();
        if (key.Distinct(StringComparer.OrdinalIgnoreCase).Count() != key.Count)
        {
            throw new FlowValidationException($"{source}: dataset.key names a column more than once.");
        }

        var label = string.IsNullOrWhiteSpace(dataset.Label) ? null : LabelColumns(dataset.Label.Trim(), source);

        // The columns an operator holds a record by, indexed by the ledger for the lookup: the dataset's own columns,
        // like the key, and each named once.
        var identity = (dataset.Identity ?? []).Select((column, i) => KeyColumn(column, $"dataset.identity[{i}]", source)).ToList();
        if (identity.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identity.Count)
        {
            throw new FlowValidationException($"{source}: dataset.identity names a column more than once.");
        }

        return new MappingDataset
        {
            System = FlowMapper.Require(dataset.System, "dataset.system", source),
            Key = key,
            Label = label,
            Identity = identity,
            IdFrom = IdFrom(dataset.IdFrom, source),
        };
    }

    /// <summary>
    /// An enrichment mapping writes under the record's <c>data</c> and <c>tags</c> alone: the id, the kind, the access list,
    /// the legal block and the lineage of the record it renders into are the keying mapping's, which an enrichment never
    /// changes, and the datasets it renders carry the record's access list and legal block. It keys no record, so it cannot
    /// find a parent or a child by the record's id: what it needs of the record it reads with <c>$base</c>.
    /// </summary>
    private static void ValidateFragment(IReadOnlyList<MappingEntry> entries, string source)
    {
        foreach (var entry in entries)
        {
            var root = entry.Target.Segments[0].Name;
            if (!FragmentRoots.Contains(root, StringComparer.Ordinal))
            {
                throw new FlowValidationException(
                    $"{source}: {entry.Where} fills {entry.Target.Text}, and an enrichment mapping writes under record.data and record.tags only: the id, kind, access list, legal block and lineage of the record it renders into are the keying mapping's, and a dataset it renders carries the record's access list and legal block.");
            }

            foreach (var node in entry.ValueNodes.Where(n => n.Source?.Kind is MappingSourceKind.Parent or MappingSourceKind.Child))
            {
                var word = node.Source!.Kind == MappingSourceKind.Parent ? ParentKey : ChildKey;
                throw new FlowValidationException(
                    $"{source}: {node.Where}: {word} finds a record by the id the keying mapping makes from its key, and an enrichment mapping keys no record; write {word} in the mapping that keys the record, and read what the record holds with {BaseKey}.");
            }
        }
    }

    /// <summary>A mapping of a whole record reads its own row; only an enrichment mapping reads the record it renders into (<c>$base</c>).</summary>
    private static void RefuseBase(IReadOnlyList<MappingEntry> entries, string source)
    {
        if (entries.SelectMany(e => e.ValueNodes).FirstOrDefault(n => n.Source?.Kind == MappingSourceKind.Base) is { } node)
        {
            throw new FlowValidationException(
                $"{source}: {node.Where}: {BaseKey} reads the record an enrichment renders into, as its mapping and the enrichments before it rendered it, and this mapping renders a record of its own; only an enrichment mapping (enrichment: true) reads one. A mapping reads its row with {FromKey}.");
        }
    }

    /// <summary>The words <c>dataset.idFrom</c> takes, as a mapping writes them.</summary>
    internal const string IdFromDeliveryKey = "deliveryKey";

    /// <inheritdoc cref="IdFromDeliveryKey"/>
    internal const string IdFromKey = "key";

    /// <summary>
    /// What <c>dataset.idFrom</c> says the OSDU id's unique segment is made from: the delivery key when it is not written,
    /// as every mapping's id always was, or the key's own values. The words compare exactly, as every other word of a
    /// mapping does.
    /// </summary>
    private static MappingIdSource IdFrom(string? written, string source) => written?.Trim() switch
    {
        null => MappingIdSource.DeliveryKey,
        IdFromDeliveryKey => MappingIdSource.DeliveryKey,
        IdFromKey => MappingIdSource.Key,
        var other => throw new FlowValidationException(
            $"{source}: dataset.idFrom is '{other}'; write {IdFromDeliveryKey} for an OSDU id made from the delivery key (the default), "
            + $"or {IdFromKey} for one made from the values of dataset.key, such as dev:reference-data--ExternalUnitOfMeasure:WELLDB::GAPI."),
    };

    /// <summary>Every lookup the mapping declares is read by a node: one nothing reads says something no record uses.</summary>
    private static void ValidateLookups(IReadOnlyList<MappingEntry> entries, IReadOnlyDictionary<string, MappingLookup> lookups, string source)
    {
        var read = entries
            .SelectMany(e => e.ValueNodes)
            .SelectMany(node => new[] { node.Source?.Lookup, node.FindAll?.Operand.Lookup?.Name })
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in lookups.Keys.Where(name => !read.Contains(name)).Order(StringComparer.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: lookups.{name} is read by no node; read it with {LookupKey}: {name}.<field> or in a {FindAllKey} line as {LookupReference}.{name}.<field>, or remove it.");
        }
    }

    /// <summary>Every text inside a static value, where parameter tokens can appear.</summary>
    internal static IEnumerable<string> StaticTexts(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    foreach (var text in StaticTexts(child))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var text in StaticTexts(item))
                    {
                        yield return text;
                    }
                }

                break;
        }
    }

    private static void Validate(List<MappingEntry> entries, IReadOnlyDictionary<string, MappingParameter> parameters, string source)
    {
        var seen = new Dictionary<TemplatePath, MappingEntry>();
        foreach (var entry in entries)
        {
            if (!seen.TryAdd(entry.Target, entry))
            {
                throw new FlowValidationException($"{source}: {entry.Where} fills {entry.Target.Text}, as {seen[entry.Target].Where} does; each variable is filled once.");
            }
        }

        var repeaters = entries.Where(e => e.IsRepeater).ToDictionary(e => e.Target);
        foreach (var entry in entries)
        {
            // A repeater's $where reads the rows it repeats; everything else under it reads the item's row.
            string? child = entry.IsRepeater ? entry.Source!.Child : null;
            if (entry.Target.Repeater is { } array)
            {
                if (!repeaters.TryGetValue(array, out var repeater))
                {
                    throw new FlowValidationException(
                        $"{source}: {entry.Where} fills a property inside the items of {array.Text}, but nothing repeats {array.Text}; lay it out under a {ForEachKey} node.");
                }

                child = repeater.Source!.Child;
            }

            if (entry.IsRepeater && entry.AppliesWhen?.Columns.FirstOrDefault(c => c.Child is not null) is { } itemColumn)
            {
                throw new FlowValidationException(
                    $"{source}: {entry.Where}: a {ForEachKey} node's {WhenKey} decides for the whole array, so it reads the dataset's own row, not {itemColumn}; {WhereKey} decides for each row.");
            }

            foreach (var column in entry.Columns)
            {
                if (column.Child is null)
                {
                    continue;
                }

                if (child is null || !string.Equals(column.Child, child, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {entry.Where} reads a column of child dataset '{column.Child}', which only a node under the {ForEachKey} over {column.Child} reads.");
                }
            }

            foreach (var name in entry.ValueNodes.SelectMany(node => StaticTexts(node.Static)).SelectMany(MappingEntry.ParameterNames))
            {
                if (!parameters.ContainsKey(name))
                {
                    throw new FlowValidationException($"{source}: {entry.Where} uses {{$param.{name}}}, but the mapping declares no parameter '{name}'.");
                }
            }

            foreach (var expression in entry.Expressions)
            {
                foreach (var name in expression.Parameters.Where(name => !parameters.ContainsKey(name)))
                {
                    throw new FlowValidationException($"{source}: {entry.Where} reads $param.{name} in '{expression}', but the mapping declares no parameter '{name}'.");
                }
            }

            foreach (var name in entry.ValueNodes.SelectMany(node => node.Modifiers).Where(m => m.Id is not null).SelectMany(m => m.Id!.Parameters))
            {
                if (!parameters.ContainsKey(name))
                {
                    throw new FlowValidationException($"{source}: {entry.Where} builds an id from {{$param.{name}}}, but the mapping declares no parameter '{name}'.");
                }
            }
        }
    }

    private static MappingEnvelope Envelope(List<MappingEntry> entries, string kind, string source)
    {
        if (DspdmKinds.Is(kind))
        {
            // A DSPDM business object row is no OSDU record: it has no access or legal block, and its business object's
            // attributes are all a mapping fills (osdu/specs/production-dspdm/INTEGRATION.md section 3.1).
            if (entries.FirstOrDefault(e => EnvelopeTargets.Any(t => t.Target == e.Target.Text)) is { } envelope)
            {
                throw new FlowValidationException(
                    $"{source}: {envelope.Where} fills {envelope.Target.Text}, and {kind} is a row of a DSPDM business object, which has no access or legal block. Remove the property.");
            }

            // The row is its data block: each attribute of the business object is a property of data, and nothing else is sent.
            if (entries.FirstOrDefault(e => e.Target.Segments.Count < 2 || e.Target.Segments[0].Name != "data") is { } outside)
            {
                throw new FlowValidationException(
                    $"{source}: {outside.Where} fills {outside.Target.Text}, and {kind} is a row of a DSPDM business object, whose attributes are the properties of "
                    + "record.data (record.data.UWI). Fill an attribute, or remove the property.");
            }

            return new MappingEnvelope([], [], [], []);
        }

        var lists = new List<IReadOnlyList<string>>();
        foreach (var (target, name, location) in EnvelopeTargets)
        {
            var entry = entries.FirstOrDefault(e => e.Target.Text == target)
                ?? throw new FlowValidationException($"{source}: every OSDU record carries {name}, so the mapping lays out {location} as a list of at least one text value.");
            var access = AccessTargets.Contains(target, StringComparer.Ordinal);
            if (entry.IsList && !access)
            {
                // The legal service checks a record's tags and countries before a run, which it can only do for a list that
                // is the same on every record.
                throw new FlowValidationException(
                    $"{source}: {entry.Where} reads values with {string.Join(" and ", entry.Parts.Where(p => !p.IsStatic).Select(p => p.Where))}, and the {name} are a literal list: the legal service checks them before a run, for every record alike.");
            }

            if (entry.Static is not JsonArray array || array.Count == 0
                || array.Any(v => v is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
            {
                throw new FlowValidationException(entry.IsList
                    ? $"{source}: {entry.Where} must list at least one literal text value, which every record carries whatever its nodes find, such as [\"{{$param.aclViewer}}\", {{ {CacheKey}: ... }}]."
                    : $"{source}: {entry.Where} must be a literal list of at least one text value, such as [value].");
            }

            if (entry.AppliesWhen is not null)
            {
                throw new FlowValidationException($"{source}: {entry.Where} applies to every record, so it takes no {WhenKey}.");
            }

            var values = array.Select(v => v!.GetValue<string>().Trim()).ToList();
            var repeated = values.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (repeated is not null)
            {
                // The legal and access lists are sets to the services that store them (openapi storage v2, Legal and Acl:
                // uniqueItems), so a repeat is a record the target may refuse; it is refused here, once.
                throw new FlowValidationException($"{source}: {entry.Where} lists '{repeated.Key}' more than once; the list is a set.");
            }

            lists.Add(values);
        }

        return new MappingEnvelope(lists[0], lists[1], lists[2], lists[3]);
    }

    /// <summary>A column of the dataset's own row as the header names it: the column's name alone, such as log_id.</summary>
    private static string KeyColumn(string text, string key, string source)
        => text?.Trim() is { } name && NamePattern().IsMatch(name)
            ? name
            : throw new FlowValidationException($"{source}: {key} names '{text}'; it names a column of the dataset's own row as it is, such as log_id.");

    /// <summary>
    /// The label with each token read as a column of the dataset's own row, <c>{wellbore_uwi}</c> or
    /// <c>{$dataset.wellbore_uwi}</c>, and kept in the first form, which the renderer and the preflight read.
    /// </summary>
    private static string LabelColumns(string label, string source) => BraceToken().Replace(label, token =>
    {
        var column = Column(token.Groups["token"].Value, TreeScope.Root, $"{source}: dataset.label token {token.Value}");
        return "{" + column.Column + "}";
    });

    /// <summary>The part of a record a search compares: its data, as the schema of the kind searched describes it.</summary>
    private const string SearchDataPrefix = "data.";

    /// <summary>
    /// A <c>searches</c> entry: the kind searched and the saved schema that says how its properties are indexed. The
    /// kind names one entity type, since one schema describes everything its query can reach, and the schema is a
    /// version of that entity type: the one searched, when the kind names a version.
    /// </summary>
    private static MappingSearch ParseSearch(string name, MappingSearchYaml? y, string source, ITemplatePins? pins)
    {
        var where = $"{source}: search '{name}'";
        if (!NamePattern().IsMatch(name))
        {
            throw new FlowValidationException(
                $"{where} is named with something other than letters, digits, underscores and hyphens; nodes read it as $search: <name>.");
        }

        var kind = y?.Kind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            throw new FlowValidationException($"{where} declares no kind; a search names the OSDU kind it looks in, such as osdu:wks:master-data--Wellbore:*.");
        }

        var segments = kind.Split(':');
        if (!OsduKind.IsValid(kind) || segments.Length != 4)
        {
            throw new FlowValidationException(
                $"{where}: kind '{kind}' is not an OSDU kind; a kind is authority:source:entityType:version, such as osdu:wks:master-data--Wellbore:*.");
        }

        if (segments.Take(3).Any(s => s.Contains('*', StringComparison.Ordinal)))
        {
            throw new FlowValidationException(
                $"{where}: kind '{kind}' leaves its authority, source or entity type open; a search looks in one entity type, whose schema says how the properties it compares are indexed, so only the version may be '*'.");
        }

        var schema = y!.Schema ?? throw new FlowValidationException(
            $"{where} pins no schema; a search pins the template of the kind it searches (schema: {{ kind: {segments[0]}:{segments[1]}:{segments[2]}:<version> }}, with the template's version when the mapping names one), which says how each property it compares is indexed, read from the template folders on the mapping's path.");
        var schemaKind = FlowMapper.Require(schema.Kind, $"searches.{name}.schema.kind", source);
        if (!FlowMapper.IsRecordKind(schemaKind))
        {
            throw new FlowValidationException(
                $"{where}: schema.kind '{schemaKind}' must be 'authority:source:entityType:major.minor.patch', the kind of a template.");
        }

        var schemaSegments = schemaKind.Split(':');
        if (!segments.Take(3).SequenceEqual(schemaSegments.Take(3), StringComparer.Ordinal))
        {
            throw new FlowValidationException(
                $"{where} searches {kind} and pins the schema of {schemaKind}, another entity type; the schema has to describe the records the query reaches.");
        }

        if (!segments[3].Contains('*', StringComparison.Ordinal) && !string.Equals(segments[3], schemaSegments[3], StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{where} searches version {segments[3]} and pins the schema of version {schemaSegments[3]}; pin the schema of the version it searches.");
        }

        var schemaVersion = string.IsNullOrWhiteSpace(schema.Version) ? null : schema.Version.Trim();
        if (schemaVersion is not null && !TemplateVersionPattern().IsMatch(schemaVersion))
        {
            throw new FlowValidationException(
                $"{where}: schema.version '{schemaVersion}' is not a template version. A version is the 16 hexadecimal characters the Templates page shows for a template file, such as 58d6bdbd9d066a06; leave it out to read the one version of the kind the nearest template folder holds.");
        }

        var description = y.Description?.Trim();
        return new MappingSearch(name, kind, Pinned(schemaKind, schemaVersion, $"{where}: schema", pins), string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>
    /// The template a document names under <paramref name="where"/>: settled by <paramref name="pins"/> when the reader has
    /// them, and otherwise the version as written, which a document read without them has to name.
    /// </summary>
    private static TemplateReference Pinned(string kind, string? version, string where, ITemplatePins? pins)
    {
        if (pins is not null)
        {
            return pins.Pin(kind, version, where);
        }

        return version is not null
            ? new TemplateReference(kind, version)
            : throw new FlowValidationException(
                $"{where} names {kind} without a version, which is settled from the template folders on the mapping's path, and the mapping was read where no template folder is known. Name the version (version: <16 hexadecimal characters>), or read the mapping in its repository.");
    }

    /// <summary>
    /// Every search a node reads names a set the mapping declares, and every set declared is read by something. A name that
    /// is not declared would otherwise fail at render, one row at a time, against a platform.
    /// </summary>
    private static void ValidateSearches(
        IReadOnlyList<MappingEntry> entries, IReadOnlyDictionary<string, MappingSearch> searches, string source)
    {
        // A search an alternative of a $coalesce node reads is read like any other.
        var searching = entries.SelectMany(e => e.ValueNodes).Where(e => e.Source?.Kind == MappingSourceKind.Search).ToList();
        foreach (var entry in searching)
        {
            var name = entry.Source!.CacheType!;
            if (!searches.ContainsKey(name))
            {
                throw new FlowValidationException(
                    searches.Count == 0
                        ? $"{source}: {entry.Where} searches {name}, and the mapping declares no searches; add a 'searches' block naming the kind each one looks in."
                        : $"{source}: {entry.Where} searches {name}, which the mapping does not declare; it declares {string.Join(", ", searches.Keys.Order(StringComparer.Ordinal))}.");
            }
        }

        // One declaration per kind: entries that look in the same kind read the same search, so a question and its answer
        // always belong to one search.
        foreach (var shared in searches.Values.GroupBy(s => s.Kind, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            throw new FlowValidationException(
                $"{source}: searches {string.Join(" and ", shared.Select(s => $"'{s.Name}'").Order(StringComparer.Ordinal))} both look in {shared.Key}; declare it once and have every node that looks there read it.");
        }

        var read = searching.Select(e => e.Source!.CacheType!).ToHashSet(StringComparer.Ordinal);
        foreach (var unread in searches.Keys.Where(k => !read.Contains(k)).Order(StringComparer.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: search '{unread}' is declared and nothing reads it; a search costs a call to the platform for every value it is asked about, so one nothing reads is a mistake rather than spare capacity.");
        }
    }

    /// <summary>The modifiers a mapping entry takes, by the name it writes them with.</summary>
    internal static readonly IReadOnlyList<string> ModifierNames = ["trim", "upper", "lower", "split", "replace", "equals", "date", "number", "id", "ref"];

    /// <summary>The settings a replace takes beside its table: what an unlisted value becomes, and a cached table's fields.</summary>
    internal static readonly IReadOnlyList<string> ReplaceSettings = ["otherwise", "match", "field"];

    /// <summary>The settings of split.</summary>
    internal static readonly IReadOnlyList<string> SplitSettings = ["separator", "part"];

    /// <summary>The settings of number.</summary>
    internal static readonly IReadOnlyList<string> NumberSettings = ["decimal", "group"];

    /// <summary>The modifiers, as a message lists them.</summary>
    private static string ModifierList => string.Join(", ", ModifierNames.Take(ModifierNames.Count - 1)) + " and " + ModifierNames[^1];

    private static string SettingList(IReadOnlyList<string> settings)
        => string.Join(", ", settings.Take(settings.Count - 1).Select(s => $"'{s}'")) + $" and '{settings[^1]}'";

    private static Modifier ParseModifier(object value, string? child, string where)
    {
        switch (value)
        {
            case string name:
                return name.Trim() switch
                {
                    "trim" => new Modifier { Kind = ModifierKind.Trim },
                    "upper" => new Modifier { Kind = ModifierKind.Upper },
                    "lower" => new Modifier { Kind = ModifierKind.Lower },
                    "date" => new Modifier { Kind = ModifierKind.Date },
                    "number" => new Modifier { Kind = ModifierKind.Number, DecimalSeparator = Rendering.NumberValues.DecimalPoint },
                    "ref" => new Modifier { Kind = ModifierKind.Ref },
                    "split" or "equals" => throw new FlowValidationException($"{where}: '{name}' needs settings, such as {Example(name)}."),
                    "replace" => throw new FlowValidationException($"{where}: 'replace' needs a table, such as {Example(name)}."),
                    "id" => throw new FlowValidationException($"{where}: 'id' needs the template the id is built from, such as {Example(name)}."),
                    _ => throw new FlowValidationException($"{where}: '{name}' is not a modifier. The modifiers are {ModifierList}."),
                };

            // A replace takes its settings beside it rather than inside its table, so no incoming value is ever read as a
            // setting: every key of the table is a value to replace.
            case IDictionary<object, object> map when map.Keys.Any(k => KeyText(k) == "replace"):
                return Replace(map, where);

            case IDictionary<object, object> map when map.Count == 1:
                {
                    var (key, settings) = map.First();
                    var modifier = KeyText(key);
                    return modifier switch
                    {
                        "split" => Split(settings, where),
                        "equals" => settings is null or IDictionary<object, object> or IList<object>
                            ? throw new FlowValidationException($"{where}: equals compares with one text, such as {Example("equals")}.")
                            : new Modifier { Kind = ModifierKind.Equals, Text = Convert.ToString(settings, CultureInfo.InvariantCulture) },
                        "date" => Date(settings, where),
                        "number" => Number(settings, where),
                        "id" => Id(settings, child, where),
                        "ref" => Ref(settings, where),
                        _ => throw new FlowValidationException($"{where}: '{modifier}' is not a modifier. The modifiers are {ModifierList}."),
                    };
                }

            case IDictionary<object, object> map when map.Count > 1:
                throw new FlowValidationException(
                    $"{where}: a modifier is one setting, such as {Example("split")}; {string.Join(" and ", map.Keys.Select(KeyText))} are written as one. Only replace takes a setting beside it (otherwise).");

            default:
                throw new FlowValidationException($"{where}: a modifier is a name, such as trim, or one setting, such as {Example("split")}.");
        }
    }

    private static string KeyText(object key) => Convert.ToString(key, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    /// <summary>A modifier's name as a message says it: id or ref.</summary>
    private static string Word(Modifier modifier) => modifier.Kind.ToString().ToLowerInvariant();

    /// <summary>
    /// A ref modifier naming the entity type the reference is of: in full (<c>reference-data--UnitOfMeasure</c>), which
    /// builds its template here, or by the entity's name alone (<c>UnitOfMeasure</c>), which picks one of the types the
    /// property points to when the template is read. Written bare, <c>ref</c> takes the one type the property points to.
    /// </summary>
    private static Modifier Ref(object? settings, string where)
    {
        var text = settings switch
        {
            string written => written.Trim(),
            null or IDictionary<object, object> or IEnumerable<object> => throw new FlowValidationException(
                $"{where}: ref names the entity type of the record it references, such as {Example("ref")}, or is written alone, as ref, to take the one the property points to."),
            _ => ScalarText(settings),
        };

        if (IdTemplate.IsEntityType(text))
        {
            return new Modifier { Kind = ModifierKind.Ref, EntityType = text, Id = IdTemplate.Reference(text) };
        }

        return EntityNamePattern().IsMatch(text)
            ? new Modifier { Kind = ModifierKind.Ref, EntityType = text }
            : throw new FlowValidationException(
                $"{where}: ref '{text}' is not an entity type; name it as the template does, such as {Example("ref")}, or in full, such as ref: reference-data--UnitOfMeasure.");
    }

    /// <summary>
    /// A scalar as the text the document wrote: a boolean as <c>true</c> or <c>false</c>, which is how YAML spells it, and a
    /// number in the invariant culture.
    /// </summary>
    private static string ScalarText(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// An id modifier: the template the id is built from, read and checked here, before any row is rendered. Its bare
    /// <c>{column}</c> tokens read the row of <paramref name="child"/>, the child dataset the node is under, or the
    /// dataset's own row.
    /// </summary>
    private static Modifier Id(object? settings, string? child, string where)
    {
        if (settings is not string text)
        {
            throw new FlowValidationException(
                $"{where}: id takes the template the id is built from as text, such as {Example("id")}; quote it, since YAML reads text that starts with '{{' as a map.");
        }

        return IdTemplate.TryParse(text, child, out var problem) is { } template
            ? new Modifier { Kind = ModifierKind.Id, Id = template }
            : throw new FlowValidationException($"{where}: id '{text}': {problem}.");
    }

    /// <summary>A date modifier: no setting reads ISO 8601, and a format is refused when it could not read a whole date.</summary>
    private static Modifier Date(object? settings, string where)
    {
        if (settings is IDictionary<object, object> or IList<object>)
        {
            throw new FlowValidationException($"{where}: date takes the input format as text, such as date: dd.MM.yyyy.");
        }

        var format = settings is null ? string.Empty : Convert.ToString(settings, CultureInfo.InvariantCulture) ?? string.Empty;
        if (format.Length == 0)
        {
            return new Modifier { Kind = ModifierKind.Date };
        }

        return Rendering.DateValues.FormatProblem(format) is { } problem
            ? throw new FlowValidationException($"{where}: the date format '{format}' {problem}.")
            : new Modifier { Kind = ModifierKind.Date, Text = format };
    }

    /// <summary>
    /// A number modifier: no setting reads '.' before the decimals and no group separator. The separators are checked here,
    /// when the mapping is read, so a mapping that could never read a number is refused before any row is rendered.
    /// </summary>
    private static Modifier Number(object? settings, string where)
    {
        if (settings is null)
        {
            return new Modifier { Kind = ModifierKind.Number, DecimalSeparator = Rendering.NumberValues.DecimalPoint };
        }

        var options = settings as IDictionary<object, object>
            ?? throw new FlowValidationException($"{where}: number takes its separators, such as {Example("number")}.");
        foreach (var option in options.Keys.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)))
        {
            if (option is null || !NumberSettings.Contains(option))
            {
                throw new FlowValidationException($"{where}: number takes {SettingList(NumberSettings)}, not '{option}'.");
            }
        }

        var point = options.TryGetValue("decimal", out var d) && d is not null
            ? Convert.ToString(d, CultureInfo.InvariantCulture) ?? string.Empty
            : Rendering.NumberValues.DecimalPoint;
        var group = options.TryGetValue("group", out var g) && g is not null ? Convert.ToString(g, CultureInfo.InvariantCulture) ?? string.Empty : null;
        if (Rendering.NumberValues.SeparatorsProblem(point, group) is { } problem)
        {
            throw new FlowValidationException($"{where}: number {problem}.");
        }

        return new Modifier { Kind = ModifierKind.Number, DecimalSeparator = point, GroupSeparator = group };
    }

    private static Modifier Split(object? settings, string where)
    {
        var options = settings as IDictionary<object, object>
            ?? throw new FlowValidationException($"{where}: split takes a separator and a part, such as {Example("split")}.");
        foreach (var option in options.Keys.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)))
        {
            if (option is null || !SplitSettings.Contains(option))
            {
                throw new FlowValidationException($"{where}: split takes {SettingList(SplitSettings)}, not '{option}'.");
            }
        }

        var separator = options.TryGetValue("separator", out var s) ? Convert.ToString(s, CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrEmpty(separator))
        {
            throw new FlowValidationException($"{where}: split needs a separator, such as {Example("split")}.");
        }

        if (!options.TryGetValue("part", out var p)
            || !int.TryParse(Convert.ToString(p, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var part)
            || part < 1)
        {
            throw new FlowValidationException($"{where}: split needs the part to keep, counting from one, such as {Example("split")}.");
        }

        return new Modifier { Kind = ModifierKind.Split, Separator = separator, Part = part };
    }

    /// <summary>
    /// A replace: its table (<c>replace: { M: m, NONE: ~ }</c>) and, beside it, what a value the table does not list becomes
    /// (<c>otherwise: ~</c>, or a text). A key is matched trimmed, so two keys that are the same once trimmed, or a key that
    /// is empty once trimmed, are refused here rather than leaving a render to choose between them.
    /// </summary>
    private static Modifier Replace(IDictionary<object, object> item, string where)
    {
        object? table = null;
        var fallback = ReplaceFallback.Keep;
        string? match = null;
        string? field = null;
        foreach (var (key, value) in item)
        {
            switch (KeyText(key))
            {
                case "replace":
                    table = value;
                    break;
                case "otherwise":
                    fallback = value switch
                    {
                        null => ReplaceFallback.Empty,
                        IDictionary<object, object> or IList<object> => throw new FlowValidationException(
                            $"{where}: replace's otherwise is one text, or ~ for no value, such as otherwise: ~."),
                        _ => ReplaceFallback.Of(ScalarText(value)),
                    };
                    break;
                case "match":
                    match = ReplaceField(value, "match", where);
                    break;
                case "field":
                    field = ReplaceField(value, "field", where);
                    break;
                case var other:
                    throw new FlowValidationException($"{where}: replace takes {SettingList(ReplaceSettings)} beside it, not '{other}'.");
            }
        }

        // A table read from the cache is named, not written: replace: $cache.<Type>.
        if (table is string named)
        {
            return new Modifier { Kind = ModifierKind.Replace, Table = CachedTable(named, match, field, where), Otherwise = fallback };
        }

        if (match is not null || field is not null)
        {
            throw new FlowValidationException(
                $"{where}: 'match' and 'field' choose the fields of a table read from the cache (replace: $cache.<Type>); a table written in the mapping matches on its keys and replaces with what each lists.");
        }

        if (table is not IDictionary<object, object> pairs || pairs.Count == 0)
        {
            throw new FlowValidationException($"{where}: replace lists incoming values and what each becomes, such as {Example("replace")}.");
        }

        var replacements = new Dictionary<string, string?>(StringComparer.Ordinal);
        var trimmed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in pairs)
        {
            var fromText = ScalarText(from);
            var key = fromText.Trim();
            if (key.Length == 0)
            {
                throw new FlowValidationException($"{where}: replace lists an empty incoming value; an empty value is never replaced, it stays empty.");
            }

            if (!trimmed.TryAdd(key, fromText))
            {
                throw new FlowValidationException(
                    $"{where}: replace lists '{trimmed[key]}' and '{fromText}', which are the same value once surrounding spaces are removed, and a value is matched trimmed.");
            }

            replacements[fromText] = to switch
            {
                null => null,
                IDictionary<object, object> or IList<object> => throw new FlowValidationException(
                    $"{where}: replace maps '{fromText}' to something that is not text; write a text, or ~ for no value."),
                _ => ScalarText(to),
            };
        }

        return new Modifier { Kind = ModifierKind.Replace, Replacements = replacements, Otherwise = fallback };
    }

    /// <summary>The cached table a replace names: <c>$cache.&lt;Type&gt;</c>, with the fields it matches on and replaces by.</summary>
    private static CachedReplaceTable CachedTable(string named, string? match, string? field, string where)
    {
        var parts = named.Trim().Split('.');
        if (parts.Length != 2 || parts[0] != CacheReference || !NamePattern().IsMatch(parts[1]))
        {
            throw new FlowValidationException(
                $"{where}: replace names '{named}', and a table read from the cache is named {CacheReference}.<Type>, such as replace: {CacheReference}.UnitAlias; a table written here is a map, such as {Example("replace")}.");
        }

        return new CachedReplaceTable(parts[1], match, field);
    }

    /// <summary>A replace's match or field: the name of a cached field, or a path into one.</summary>
    private static string ReplaceField(object? value, string setting, string where)
    {
        var text = value is null or IDictionary<object, object> or IList<object> ? null : ScalarText(value).Trim();
        if (string.IsNullOrEmpty(text) || text.Split('.').Any(part => !FieldPattern().IsMatch(part)))
        {
            throw new FlowValidationException($"{where}: replace's {setting} names a field of the cached table, such as {setting}: {(setting == "match" ? "mnemonic" : "family")}.");
        }

        return text;
    }

    private static string Example(string modifier) => modifier switch
    {
        "split" => "split: { separator: \",\", part: 1 }",
        "replace" => "replace: { GAPI: gAPI }",
        "number" => "number: { decimal: \",\", group: \" \" }",
        "id" => "id: " + IdTemplate.Example,
        "ref" => "ref: UnitOfMeasure",
        _ => "equals: REGULAR",
    };

    private static string? Quoted(string text)
        => text.Length >= 2 && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"')) ? text[1..^1] : null;

    /// <summary>
    /// Converts a YAML value to JSON: mappings to objects, sequences to arrays, and scalars keeping the type YAML read them
    /// as. YAML reads an unquoted whole number into the smallest integer type that holds it, so every integer type is a number.
    /// </summary>
    private static JsonNode? StaticValue(object? value, string where) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong number when number <= long.MaxValue => JsonValue.Create((long)number),
        ulong number => JsonValue.Create(number),
        // YAML reads .nan and .inf as floating-point values, and a record cannot carry either, inside a list or an object included.
        double number when !double.IsFinite(number) => throw NonFiniteStatic(number, where),
        double number => JsonValue.Create(number),
        float number when !float.IsFinite(number) => throw NonFiniteStatic(number, where),
        float number => JsonValue.Create(Rendering.NumberValues.Widen(number)),
        decimal number => JsonValue.Create((double)number),
        IDictionary<object, object> map => new JsonObject(map.Select(kv => new KeyValuePair<string, JsonNode?>(
            Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? string.Empty,
            StaticValue(kv.Value, where) ?? throw new FlowValidationException($"{where}: the literal's '{kv.Key}' has no value.")))),
        IEnumerable<object> list => new JsonArray(list.Select(item => StaticValue(item, where)
            ?? throw new FlowValidationException($"{where}: a literal list holds an empty item.")).ToArray()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static FlowValidationException NonFiniteStatic(object number, string where)
        => new($"{where}: the literal {Convert.ToString(number, CultureInfo.InvariantCulture)} is NaN or Infinity, which a JSON record cannot carry (RFC 8259).");

    [GeneratedRegex(@"^[0-9a-f]{16}$")]
    private static partial Regex TemplateVersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex NamePattern();

    /// <summary>The name of an OSDU entity, as a relationship names it after its group: letters, digits and '_'.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex EntityNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^\s*(?<field>[^=\s]+)\s*=\s*(?<operand>.+?)\s*$")]
    private static partial Regex FindByPattern();

    [GeneratedRegex(@"^(?<column>\S+)\s+is\s+(?<not>not\s+)?(?<rest>.+)$")]
    private static partial Regex ConditionPattern();

    /// <summary>A token of a loaded label: a column of the dataset's own row, <c>{wellbore_uwi}</c>.</summary>
    [GeneratedRegex(@"\{(?<column>[A-Za-z0-9_\-]+)\}")]
    internal static partial Regex LabelToken();
}
