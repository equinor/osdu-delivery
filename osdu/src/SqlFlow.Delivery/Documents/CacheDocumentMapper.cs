using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Documents;

/// <summary>Maps a parsed cache document onto <see cref="CacheDefinition"/> and validates what YAML cannot.</summary>
internal static class CacheMapper
{
    public static CacheDefinition Map(CacheYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var headers = new Dictionary<string, string>(src.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        var partitions = MapPartitions(y.Partitions, source);

        if (partitions.Count > 0)
        {
            // A flow that names its partitions builds a cache for each, and every refresh sets the header to the one it
            // builds; a header written beside them could only disagree.
            if (headers.ContainsKey(FlowMapper.PartitionHeader))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers names '{FlowMapper.PartitionHeader}', and the flow names its partitions: every refresh sets the header to the partition it builds. Remove the header.");
            }
        }
        else if (headers.TryGetValue(FlowMapper.PartitionHeader, out var partition))
        {
            // The partition is hard-coded in the header: it names the cache the flow fills, so it has to be one a cache can be
            // kept under, and the search service requires it on every request.
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers.{FlowMapper.PartitionHeader} is empty. Name the partition the flow fills, or take the header out so the flow builds a cache for every partition registered with the catalog.");
            }

            _ = CacheScope.Normalize(partition, $"{source}: source.headers");
        }

        // Neither a list nor a header: the flow builds a cache for every partition registered with the catalog, and every
        // refresh sets the header to the one it builds.
        var followsRegistry = partitions.Count == 0 && !headers.ContainsKey(FlowMapper.PartitionHeader);

        if (y.MakeCurrent is not null)
        {
            throw new FlowValidationException(
                $"{source}: makeCurrent is not a setting any more: every version a refresh writes becomes the current version of its partition's cache. Remove it.");
        }

        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new FlowParameter { Required = kv.Value.Required, Default = kv.Value.Default, Description = kv.Value.Description },
            StringComparer.Ordinal);
        if ((partitions.Count > 0 || followsRegistry) && parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', and the flow {(followsRegistry ? "serves every registered partition" : "names its partitions")}: a run names the partition it refreshes under that value. Rename the parameter.");
        }

        var defaultMode = FlowMapper.ParseEnum(y.OnChange, CacheChangeMode.Auto, "onChange", source);
        var types = MapTypes(y.Types, defaultMode, parameters, partitions, followsRegistry, source);

        // The platform is reached only for the types searched on it, so a flow of lookup tables alone needs no endpoint, and
        // a flow that names one it never searches is told so rather than left holding a setting that does nothing.
        var searched = types.Any(t => t.Origin == CacheOrigin.Osdu);
        var endpoint = string.IsNullOrWhiteSpace(src.Endpoint) ? null : src.Endpoint.Trim();
        if (searched && endpoint is null)
        {
            throw new FlowValidationException($"{source}: source.endpoint is required: the flow declares a type searched on OSDU, by its kind.");
        }

        if (!searched && (endpoint is not null || src.Auth is not null))
        {
            throw new FlowValidationException(
                $"{source}: source.endpoint and source.auth reach the OSDU platform, and the flow declares no type searched there; every type it declares is a lookup table. Remove them.");
        }

        // The database is reached only for the table types, checked the way a delivery flow's connection is: references
        // only, never a literal secret.
        var tables = types.Any(t => t.Origin == CacheOrigin.Table);
        var connection = string.IsNullOrWhiteSpace(src.Connection) ? null : src.Connection.Trim();
        if (tables && connection is null)
        {
            throw new FlowValidationException($"{source}: source.connection is required: the flow declares a type read from an ingestion table.");
        }

        if (!tables && connection is not null)
        {
            throw new FlowValidationException($"{source}: source.connection reaches the ingestion database, and the flow declares no type read from a table there. Remove it.");
        }

        if (connection is not null)
        {
            IngestionConnection.CheckDeclared(connection, source);
        }

        return new CacheDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Parameters = parameters,
            Partitions = partitions,
            FollowsRegistry = followsRegistry,
            Source = new CacheSource
            {
                Endpoint = endpoint,
                Connection = connection,
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
                Paging = FlowMapper.ParseEnum(src.Paging, SearchPaging.Cursor, "source.paging", source),
            },
            Types = types,
            OnChange = defaultMode,
            RetentionDays = CacheRetention.Check(y.RetentionDays, source),
            Reliability = FlowMapper.MapReliability(y.Reliability, "a cache flow", takesRecordsAtOnce: false, source),
        };
    }

    /// <summary>
    /// Maps the declared types. A type's name and entity type are derived from its kind when they are not spelled out, so
    /// the common case is a kind and a list of paths.
    /// </summary>
    private static List<ReferenceTypeSpec> MapTypes(
        IReadOnlyList<CachedTypeYaml>? declared, CacheChangeMode defaultMode, IReadOnlyDictionary<string, FlowParameter> parameters,
        IReadOnlyList<string> partitions, bool followsRegistry, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: types is required: list the types the cache holds, each an OSDU kind with the paths of a record to keep, a dictionary, or an ingestion table.");
        }

        var types = new List<ReferenceTypeSpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var where = $"types[{i}]";
            var type = declared[i];
            var origins = new[] { type.Kind, type.Dictionary, type.Table, type.Dimension }.Count(v => !string.IsNullOrWhiteSpace(v));
            if (origins > 1)
            {
                throw new FlowValidationException(
                    $"{source}: {where} names more than one origin; a type has one: a kind searched on OSDU, a dictionary kept in the repository, a table an ingestion flow loads, or a dimension a dimension flow builds.");
            }

            var builtFor = MapTypePartitions(type.Partitions, partitions, followsRegistry, where, source);
            if (!string.IsNullOrWhiteSpace(type.Dimension) || !string.IsNullOrWhiteSpace(type.DimensionFlow))
            {
                types.Add(Validated(DimensionType(type, defaultMode, where, source) with { Partitions = builtFor }, where, source));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(type.Dictionary))
            {
                types.Add(Validated(DictionaryType(type, defaultMode, where, source) with { Partitions = builtFor }, where, source));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(type.Table))
            {
                types.Add(Validated(TableType(type, defaultMode, where, source) with { Partitions = builtFor }, where, source));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(type.Key))
            {
                throw new FlowValidationException($"{source}: {where} names a key, which only a table type takes: an OSDU record is kept under its id.");
            }

            var kind = string.IsNullOrWhiteSpace(type.Kind)
                ? throw new FlowValidationException(
                    $"{source}: {where} needs a kind (the OSDU kind whose records the type caches), a dictionary (a lookup table kept in the repository) or a table (an ingestion table SQLFlow's flows load).")
                : type.Kind!.Trim();

            if (!OsduKind.IsValid(kind))
            {
                throw new FlowValidationException($"{source}: {where}.kind '{kind}' is not authority:source:entityType:version (wildcards allowed per segment).");
            }

            var entityType = string.IsNullOrWhiteSpace(type.EntityType)
                ? OsduKind.EntityType(kind) ?? throw new FlowValidationException($"{source}: {where} needs an entityType, because kind '{kind}' does not name one.")
                : type.EntityType!.Trim();
            var query = string.IsNullOrWhiteSpace(type.Query) ? "*" : type.Query!.Trim();
            foreach (var token in FlowMapper.Tokens(query))
            {
                if (!parameters.ContainsKey(token))
                {
                    throw new FlowValidationException($"{source}: {where}.query uses '{{{token}}}', which is not declared under parameters.");
                }
            }

            var spec = new ReferenceTypeSpec
            {
                Name = string.IsNullOrWhiteSpace(type.Name) ? ShortNameOf(entityType) : type.Name!.Trim(),
                EntityType = entityType,
                Kind = kind,
                Query = query,
                OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
                Fields = MapFields(type.Fields, $"{where}.fields", source),
                Partitions = builtFor,
            };

            types.Add(Validated(spec, where, source));
        }

        CheckTypeNames(types, partitions, followsRegistry, source);
        return types;
    }

    /// <summary>
    /// The partitions a flow builds a cache for (osdu/docs/reference/flow/cache.md, Which partitions a flow fills),
    /// each by its data-partition-id; empty when the document names none.
    /// </summary>
    private static IReadOnlyList<string> MapPartitions(IReadOnlyList<string>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        var names = declared.Select((name, i) => PartitionNames.Check(name, string.Create(CultureInfo.InvariantCulture, $"{source}: partitions[{i}]"))).ToList();
        PartitionNames.CheckList(names, $"{source}: partitions");
        return names;
    }

    /// <summary>
    /// The partitions a type narrows its flow's partitions to: for a flow that names its partitions, some of them, in the
    /// spelling the flow names them in; for a flow that serves every registered partition, the partitions this type is built
    /// for (a partition-specific type, such as one that only prod holds). Empty when the type names none and is built for
    /// every partition its flow serves.
    /// </summary>
    private static IReadOnlyList<string> MapTypePartitions(
        IReadOnlyList<string>? declared, IReadOnlyList<string> partitions, bool followsRegistry, string where, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (partitions.Count == 0 && !followsRegistry)
        {
            throw new FlowValidationException(
                $"{source}: {where}.partitions narrows the partitions of its flow, and the flow's one partition is its header's; name the partitions under 'partitions' at the top of the document, or take the header out so the flow serves every registered partition.");
        }

        var names = new List<string>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{source}: {where}.partitions[{i}]");
            var name = PartitionNames.Check(declared[i], at);
            names.Add(followsRegistry
                ? name
                : partitions.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FlowValidationException($"{at} '{name}' is not a partition of the flow; it names {PartitionNames.Listed(partitions)}."));
        }

        PartitionNames.CheckList(names, $"{source}: {where}.partitions");
        return names;
    }

    /// <summary>
    /// A mapping reads a type by its name, so a partition's cache holds each name once. A flow whose partition is its
    /// header's keeps one cache and so unique names; one naming its partitions keeps them unique per partition, and each of
    /// its partitions is built at least one type. One serving every registered partition keeps them unique in every
    /// partition a type names and in every other, where only the types that name no partition are built.
    /// </summary>
    private static void CheckTypeNames(IReadOnlyList<ReferenceTypeSpec> types, IReadOnlyList<string> partitions, bool followsRegistry, string source)
    {
        if (followsRegistry)
        {
            var named = types.SelectMany(t => t.Partitions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var (partition, built) in named.Select(p => (p, types.Where(t => t.IsBuiltFor(p)).ToList()))
                .Append(("any other registered partition", types.Where(t => t.Partitions.Count == 0).ToList())))
            {
                var twice = built.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (twice.Count > 0)
                {
                    throw new FlowValidationException(
                        $"{source}: types declares {string.Join(", ", twice)} more than once for {(partition.Contains(' ', StringComparison.Ordinal) ? partition : $"partition '{partition}'")}; a mapping reads a type by its name, so a partition's cache holds each name once. Give the declarations partitions that do not overlap.");
                }
            }

            return;
        }

        if (partitions.Count == 0)
        {
            if (types.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Count)
            {
                throw new FlowValidationException($"{source}: types declares the same type name more than once; a mapping reads a type by its name, so each needs its own.");
            }

            return;
        }

        foreach (var partition in partitions)
        {
            var built = types.Where(t => t.IsBuiltFor(partition)).ToList();
            if (built.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: no type is built for partition '{partition}'; every type narrows its partitions to others. Remove the partition from 'partitions', or build a type for it.");
            }

            var twice = built.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (twice.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: types declares {string.Join(", ", twice)} more than once for partition '{partition}'; a mapping reads a type by its name, so a partition's cache holds each name once. Give the declarations partitions that do not overlap.");
            }
        }
    }

    /// <summary>
    /// A type holding a dictionary document: named after the dictionary unless it says otherwise, kept as a lookup table, and
    /// taking none of an OSDU type's settings, because the document itself names the key and the fields.
    /// </summary>
    private static ReferenceTypeSpec DictionaryType(CachedTypeYaml type, CacheChangeMode defaultMode, string where, string source)
    {
        var dictionary = type.Dictionary!.Trim();
        foreach (var (setting, value) in new (string, object?)[] { ("kind", type.Kind), ("entityType", type.EntityType), ("query", type.Query), ("fields", type.Fields), ("key", type.Key) })
        {
            if (value is not null)
            {
                throw new FlowValidationException(
                    $"{source}: {where} holds dictionary {dictionary}, which takes no '{setting}': the dictionary document names its key and its fields, and it is kept as a lookup table, not searched for.");
            }
        }

        var name = string.IsNullOrWhiteSpace(type.Name) ? dictionary : type.Name!.Trim();
        return new ReferenceTypeSpec
        {
            Name = name,
            EntityType = ReferenceType.LookupEntityType(name),
            Origin = CacheOrigin.Dictionary,
            Dictionary = dictionary,
            OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
        };
    }

    /// <summary>
    /// A type holding a dimension's values (osdu/docs/reference/flow/cache.md, Dimension values (<c>dimension</c>,
    /// <c>dimensionFlow</c>)): the dimension and the dimension flow declaring it, read from the partition's ledger as the
    /// dimension's last build left it. Named after the dimension unless it says otherwise, kept as a lookup table keyed by
    /// each value, with its keys, records and filter beside it, and the attributes its <c>fields</c> name (attributes the
    /// dimension reads, by name); it takes none of the other origins' settings.
    /// </summary>
    private static ReferenceTypeSpec DimensionType(CachedTypeYaml type, CacheChangeMode defaultMode, string where, string source)
    {
        if (string.IsNullOrWhiteSpace(type.Dimension) || string.IsNullOrWhiteSpace(type.DimensionFlow))
        {
            throw new FlowValidationException(
                $"{source}: {where} holds a dimension's members and names both: dimension (the dimension's name) and dimensionFlow (the dimension flow that declares it).");
        }

        var dimension = type.Dimension!.Trim();
        var flow = type.DimensionFlow!.Trim();
        if (!SelectableNames.IsName(dimension))
        {
            throw new FlowValidationException($"{source}: {where}.dimension '{dimension}' is not a dimension's name: {SelectableNames.Rule}.");
        }

        foreach (var (setting, value) in new (string, object?)[] { ("entityType", type.EntityType), ("query", type.Query), ("key", type.Key) })
        {
            if (value is not null)
            {
                throw new FlowValidationException(
                    $"{source}: {where} holds dimension {dimension}, which takes no '{setting}': its rows are the dimension's values, keyed by '{DimensionColumns.Value}' with '{string.Join("' and '", DimensionColumns.Fields)}' beside it, and the attributes 'fields' names.");
            }
        }

        // The attributes each row carries beside the fixed columns, named as the dimension declares them.
        var attributes = new List<ReferenceFieldSpec>();
        foreach (var (field, index) in (type.Fields ?? []).Select((f, i) => (f, i)))
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{where}.fields[{index}]");
            if (field is not string text || !DimensionAttributeSpec.IsName(text.Trim()))
            {
                throw new FlowValidationException(
                    $"{source}: {at} is not an attribute's name; a dimension type's fields name attributes its dimension reads (fields: [Country, Field]).");
            }

            var attribute = text.Trim();
            if (DimensionAttributeSpec.Reserved.Contains(attribute) || attributes.Any(a => string.Equals(a.Name, attribute, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException(
                    $"{source}: {at} '{attribute}' is a column the rows hold already, or is named twice; name the attributes of the dimension to carry.");
            }

            attributes.Add(new ReferenceFieldSpec(attribute, attribute));
        }

        var name = string.IsNullOrWhiteSpace(type.Name) ? dimension : type.Name!.Trim();
        return new ReferenceTypeSpec
        {
            Name = name,
            EntityType = ReferenceType.LookupEntityType(name),
            Origin = CacheOrigin.Dimension,
            Dimension = dimension,
            DimensionFlow = flow,
            Key = DimensionColumns.Value,
            Fields = [.. DimensionColumns.FieldSpecs(), .. attributes],
            OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
        };
    }

    /// <summary>
    /// A type reading an ingestion table: the table, the column each row is keyed by, and the columns kept beside it, each
    /// bare or as <c>{ column: ..., as: ... }</c>. Named after the table unless it says otherwise, and kept as a lookup table.
    /// </summary>
    private static ReferenceTypeSpec TableType(CachedTypeYaml type, CacheChangeMode defaultMode, string where, string source)
    {
        var table = type.Table!.Trim();
        FlowMapper.CheckObject(table, $"{where}.table", source);
        foreach (var (setting, value) in new (string, object?)[] { ("entityType", type.EntityType), ("query", type.Query) })
        {
            if (value is not null)
            {
                throw new FlowValidationException(
                    $"{source}: {where} reads table {table}, which takes no '{setting}': it is kept as a lookup table holding every row of the table, not searched for.");
            }
        }

        var key = string.IsNullOrWhiteSpace(type.Key)
            ? throw new FlowValidationException($"{source}: {where}.key is required: the column of {table} each row is keyed by and a mapping finds the row by.")
            : type.Key!;
        FlowMapper.CheckColumn(key, $"{where}.key", source);
        var name = string.IsNullOrWhiteSpace(type.Name) ? SourceObjectName.Parse(table).Name : type.Name!.Trim();
        return new ReferenceTypeSpec
        {
            Name = name,
            EntityType = ReferenceType.LookupEntityType(name),
            Origin = CacheOrigin.Table,
            Table = table,
            Key = key,
            Fields = MapColumns(type.Fields, $"{where}.fields", table, source),
            OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
        };
    }

    /// <summary>A table type's columns: each a column name, kept under it, or a column with the name to keep it under.</summary>
    private static List<ReferenceFieldSpec> MapColumns(IReadOnlyList<object>? fields, string where, string table, string source)
    {
        if (fields is null || fields.Count == 0)
        {
            throw new FlowValidationException($"{source}: {where} lists no column of {table} to keep beside the key; list the columns a mapping reads.");
        }

        var mapped = new List<ReferenceFieldSpec>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            string? column = null;
            string? name = null;
            switch (fields[i])
            {
                case string bare:
                    column = bare;
                    break;
                case IDictionary<object, object?> entry:
                    foreach (var (settingKey, value) in entry)
                    {
                        switch (settingKey.ToString())
                        {
                            case "column":
                                column = value?.ToString();
                                break;
                            case "as":
                                name = value?.ToString();
                                break;
                            default:
                                throw new FlowValidationException($"{source}: {where}[{i}] has no '{settingKey}' setting; a table column takes 'column' and 'as'.");
                        }
                    }

                    break;
                default:
                    throw new FlowValidationException($"{source}: {where}[{i}] is neither a column name nor a 'column'/'as' pair.");
            }

            if (string.IsNullOrWhiteSpace(column))
            {
                throw new FlowValidationException($"{source}: {where}[{i}] needs a column.");
            }

            FlowMapper.CheckColumn(column, $"{where}[{i}]", source);
            mapped.Add(new ReferenceFieldSpec(column, string.IsNullOrWhiteSpace(name) ? column : name.Trim()));
        }

        return mapped;
    }

    private static ReferenceTypeSpec Validated(ReferenceTypeSpec spec, string where, string source)
    {
        try
        {
            spec.Validate();
        }
        catch (FlowValidationException ex)
        {
            throw new FlowValidationException($"{source}: {where}: {ex.Message}", ex);
        }

        return spec;
    }

    /// <summary>Reads a field list where an entry is either a path or a path with the name to cache it under.</summary>
    private static List<ReferenceFieldSpec> MapFields(IReadOnlyList<object>? fields, string where, string source)
    {
        if (fields is null || fields.Count == 0)
        {
            throw new FlowValidationException($"{source}: {where} lists no paths to cache.");
        }

        var mapped = new List<ReferenceFieldSpec>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            switch (fields[i])
            {
                case string path when !string.IsNullOrWhiteSpace(path):
                    mapped.Add(new ReferenceFieldSpec(path));
                    break;
                case IDictionary<object, object?> entry:
                    mapped.Add(MapField(entry, $"{where}[{i}]", source));
                    break;
                default:
                    throw new FlowValidationException($"{source}: {where}[{i}] is neither a path nor a 'path'/'as' pair.");
            }
        }

        return mapped;
    }

    private static ReferenceFieldSpec MapField(IDictionary<object, object?> entry, string where, string source)
    {
        string? path = null;
        string? name = null;
        foreach (var (key, value) in entry)
        {
            switch (key.ToString()?.ToLowerInvariant())
            {
                case "path":
                    path = value?.ToString();
                    break;
                case "as":
                    name = value?.ToString();
                    break;
                default:
                    throw new FlowValidationException($"{source}: {where} has no '{key}' setting; a cached field takes 'path' and 'as'.");
            }
        }

        return string.IsNullOrWhiteSpace(path)
            ? throw new FlowValidationException($"{source}: {where} needs a 'path'.")
            : new ReferenceFieldSpec(path!, name);
    }

    /// <summary>The short name a mapping uses (reference-data--UnitOfMeasure becomes UnitOfMeasure).</summary>
    private static string ShortNameOf(string entityType)
    {
        var separator = entityType.LastIndexOf("--", StringComparison.Ordinal);
        return separator >= 0 ? entityType[(separator + 2)..] : entityType;
    }
}
