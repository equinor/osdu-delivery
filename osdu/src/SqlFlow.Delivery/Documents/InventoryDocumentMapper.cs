using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a parsed inventory document onto <see cref="InventoryFlowDefinition"/> and validates what YAML cannot
/// (osdu/docs/reference/flow/inventory.md, Top-level keys): how the flow reads OSDU, every inventory's kind and query, the
/// owners and the bound on the missing checks. What needs OSDU (the kinds a wildcard names, whether storage's admin route
/// answers) is found out when a build or a plan reads it.
/// </summary>
internal static class InventoryMapper
{
    /// <summary>The ways a source reads OSDU, as a document writes them.</summary>
    internal static IReadOnlyList<string> ReadModes { get; } = ["search", "storage"];

    /// <summary>The versions an inventory keeps, as a document writes them.</summary>
    internal static IReadOnlyList<string> VersionModes { get; } = ["latest", "all"];

    public static InventoryFlowDefinition Map(InventoryFlowYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var headers = new Dictionary<string, string>(src.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        var partitions = MapPartitions(y.Partitions, source);
        if (partitions.Count > 0)
        {
            if (headers.ContainsKey(FlowMapper.PartitionHeader))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers names '{FlowMapper.PartitionHeader}', and the flow names its partitions: every run sets the header to the partition it reads. Remove the header.");
            }
        }
        else if (headers.TryGetValue(FlowMapper.PartitionHeader, out var partition))
        {
            if (string.IsNullOrWhiteSpace(partition))
            {
                throw new FlowValidationException(
                    $"{source}: source.headers.{FlowMapper.PartitionHeader} is empty. Name the partition the flow reads, or take the header out so the flow reads every partition registered with the catalog.");
            }

            _ = CacheScope.Normalize(partition, $"{source}: source.headers");
        }

        var followsRegistry = partitions.Count == 0 && !headers.ContainsKey(FlowMapper.PartitionHeader);
        var parameters = FlowParameterDeclarations.Map(y.Parameters, source);
        if (parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', which an inventory flow keeps for the partition a run reads: {{partition}} in a query is always that partition. Rename the parameter.");
        }

        var read = MapRead(src.Read, source);
        var maxMissingChecks = y.MaxMissingChecks ?? InventoryFlowDefinition.DefaultMaxMissingChecks;
        if (maxMissingChecks is < 0 or > InventoryFlowDefinition.MaxMissingChecksCeiling)
        {
            throw new FlowValidationException(
                $"{source}: maxMissingChecks is {maxMissingChecks}; it is how many ids a ledger expects that one build reads from storage to tell missing from merely unlisted, between 0 (none) and {InventoryFlowDefinition.MaxMissingChecksCeiling.ToString("N0", CultureInfo.InvariantCulture)}.");
        }

        var flow = new InventoryFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            Parameters = parameters,
            Source = new InventorySource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
                Read = read,
                QueryPath = ServicePath(src.QueryPath, InventorySource.DefaultQueryPath, "source.queryPath", source),
                SearchPath = ServicePath(src.SearchPath, InventorySource.DefaultSearchPath, "source.searchPath", source),
                Paging = FlowMapper.ParseEnum(src.Paging, SearchPaging.Cursor, "source.paging", source),
                RecordQueryPath = ServicePath(src.RecordQueryPath, InventorySource.DefaultRecordQueryPath, "source.recordQueryPath", source),
                HeadersPath = ServicePath(src.HeadersPath, InventorySource.DefaultHeadersPath, "source.headersPath", source),
                VersionsPath = ServicePath(src.VersionsPath, InventorySource.DefaultVersionsPath, "source.versionsPath", source),
                SchemaPath = ServicePath(src.SchemaPath, InventorySource.DefaultSchemaPath, "source.schemaPath", source),
                DeletePath = RecordPath(src.DeletePath, InventorySource.DefaultDeletePath, "source.deletePath", source),
                BulkDeletePath = ServicePath(src.BulkDeletePath, InventorySource.DefaultBulkDeletePath, "source.bulkDeletePath", source),
                PurgePath = RecordPath(src.PurgePath, InventorySource.DefaultPurgePath, "source.purgePath", source),
            },
            Partitions = partitions,
            FollowsRegistry = followsRegistry,
            Owners = MapOwners(y.Owners, source),
            MaxMissingChecks = maxMissingChecks,
            Inventories = MapInventories(y.Inventories, parameters, read, source),
            Removal = MapRemoval(y.Removal, source),
            Reliability = FlowMapper.MapReliability(y.Reliability, "an inventory flow", takesRecordsAtOnce: true, source),
            Parallelism = FlowMapper.MapRunParallelism(y.Parallelism, "the version lists a build reads at once", source),
        };

        return flow;
    }

    private static IReadOnlyList<string> MapPartitions(IReadOnlyList<string>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        var names = declared.Select((n, i) => PartitionNames.Check(n, string.Create(CultureInfo.InvariantCulture, $"{source}: partitions[{i}]"))).ToList();
        PartitionNames.CheckList(names, $"{source}: partitions");
        return names;
    }

    private static InventoryRead MapRead(string? declared, string source) => (declared?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "search" => InventoryRead.Search,
        "storage" => InventoryRead.Storage,
        var other => throw new FlowValidationException(
            $"{source}: source.read is '{other}'; it is search (the index, a viewer's entitlements, the default) or storage (every active record, the storage service's admin role)."),
    };

    private static IReadOnlyList<string> MapOwners(IReadOnlyList<string>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count > InventoryFlowDefinition.MaxOwners)
        {
            throw new FlowValidationException(
                $"{source}: owners lists {declared.Count} identities; a flow names at most {InventoryFlowDefinition.MaxOwners}, the identities its records are written as.");
        }

        var owners = new List<string>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var owner = declared[i]?.Trim();
            if (string.IsNullOrEmpty(owner) || owner.Length > DeliveryModel.MaxInventoryUserLength || owner.Any(char.IsWhiteSpace))
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: owners[{i}] is not an identity: OSDU's createUser of a record, at most {DeliveryModel.MaxInventoryUserLength} characters, no whitespace (a user's e-mail, an application's client id)."));
            }

            if (owners.Contains(owner, StringComparer.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{source}: owners[{i}] '{owner}' is listed twice."));
            }

            owners.Add(owner);
        }

        return owners;
    }

    private static IReadOnlyList<InventorySpec> MapInventories(List<InventoryYaml>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, InventoryRead read, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: inventories must list at least one inventory; an inventory flow without inventories has nothing to read.");
        }

        if (declared.Count > InventoryFlowDefinition.MaxInventories)
        {
            throw new FlowValidationException(
                $"{source}: inventories lists {declared.Count} inventories; one flow holds at most {InventoryFlowDefinition.MaxInventories}. Split them over several flows.");
        }

        var inventories = new List<InventorySpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"inventories[{i}]");
            var d = declared[i] ?? throw new FlowValidationException($"{source}: {at} is empty.");
            var name = FlowMapper.Require(d.Name, at + ".name", source);
            if (!SelectableNames.IsName(name))
            {
                throw new FlowValidationException($"{source}: {at}.name '{name}' is not an inventory name: {SelectableNames.Rule}.");
            }

            if (inventories.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException(
                    $"{source}: {at} is named '{name}', as an earlier inventory is; a run, a page and a report name each inventory by its name, so each needs its own.");
            }

            var where = $"{at} '{name}'";
            var kind = FlowMapper.Require(d.Kind, at + ".kind", source);
            if (!OsduKind.IsValid(kind) || kind.Length > DeliveryModel.MaxInventoryKindLength)
            {
                throw new FlowValidationException($"{source}: {where}: kind '{kind}' is not authority:source:entityType:version (wildcards allowed per segment).");
            }

            var query = Optional(d.Query);
            if (query is not null)
            {
                if (read == InventoryRead.Storage)
                {
                    throw new FlowValidationException(
                        $"{source}: {where}: query narrows a search, and the flow reads storage (source.read: storage), which lists every record of a kind and takes no query. Take the query out, or read through search.");
                }

                if (query.Length > DeliveryModel.MaxInventoryQueryLength)
                {
                    throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: query is {query.Length} characters; an inventory keeps at most {DeliveryModel.MaxInventoryQueryLength}."));
                }

                foreach (var token in FlowMapper.Tokens(query))
                {
                    if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
                    {
                        throw new FlowValidationException($"{source}: {where}: query uses '{{{token}}}', which is not declared under parameters.");
                    }
                }
            }

            var versions = (d.Versions?.Trim().ToLowerInvariant()) switch
            {
                null or "" or "latest" => InventoryVersions.Latest,
                "all" => InventoryVersions.All,
                var other => throw new FlowValidationException(
                    $"{source}: {where}: versions is '{other}'; it is latest (the latest version of each record, the default) or all (every version storage keeps, read for what is new or moved)."),
            };

            inventories.Add(new InventorySpec { Name = name, Description = Optional(d.Description), Kind = kind, Query = query, Versions = versions });
        }

        return inventories;
    }

    /// <summary>
    /// The removal a flow allows: at least one of the removable findings, each once, and whether a removal may purge. A flow
    /// that leaves the key out allows none, so nothing it found can be removed through it.
    /// </summary>
    private static InventoryRemovalPolicy? MapRemoval(InventoryRemovalYaml? declared, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var removable = string.Join(", ", InventoryRemovals.Removable);
        if (declared.Findings is null || declared.Findings.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: removal.findings must name the findings whose ids an operator may remove from OSDU, of {removable}; take removal out for a flow that only reads OSDU.");
        }

        var findings = new List<string>(declared.Findings.Count);
        for (var i = 0; i < declared.Findings.Count; i++)
        {
            var finding = declared.Findings[i]?.Trim().ToLowerInvariant();
            if (finding == InventoryFindings.Gone)
            {
                throw new FlowValidationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source}: removal.findings[{i}] is '{declared.Findings[i]}'; a gone id is one OSDU no longer serves, so there is nothing of it in OSDU to remove. Its row is removed from the inventory, whatever the flow declares; removal.findings names what an operator may remove from OSDU, of {removable}."));
            }

            if (!InventoryRemovals.IsRemovable(finding))
            {
                throw new FlowValidationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source}: removal.findings[{i}] is '{declared.Findings[i]}'; the ids an inventory may remove are those of {removable}: what OSDU serves that no ledger holds live. Every other finding is acted on through its ledger."));
            }

            if (findings.Contains(finding!, StringComparer.Ordinal))
            {
                throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{source}: removal.findings[{i}] '{finding}' is listed twice."));
            }

            findings.Add(finding!);
        }

        return new InventoryRemovalPolicy { Findings = findings, Purge = declared.Purge ?? false };
    }

    /// <summary>A path of one record's endpoint: a service path that names the record as <c>{id}</c>.</summary>
    private static string RecordPath(string? declared, string fallback, string key, string source)
    {
        var path = ServicePath(declared, fallback, key, source);
        return path.Contains("{id}", StringComparison.Ordinal)
            ? path
            : throw new FlowValidationException($"{source}: {key} '{path}' must name the record it acts on as {{id}}, as '{fallback}' does.");
    }

    private static string ServicePath(string? declared, string fallback, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return fallback;
        }

        var path = declared.Trim();
        if (!path.StartsWith('/') || path.Any(char.IsWhiteSpace) || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: {key} '{path}' must be a path under the endpoint, starting with '/', with no whitespace, query or fragment.");
        }

        return path.TrimEnd('/').Length == 0 ? path : path.TrimEnd('/');
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
