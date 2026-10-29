using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a parsed download document onto <see cref="DownloadFlowDefinition"/> and validates what YAML cannot
/// (osdu/docs/reference/flow/download.md, Top-level keys): how the flow reads OSDU, the partitions, where the files go, and
/// every download's kind and query. The source is read as an inventory flow's is (<see cref="InventoryMapper"/>), so the two
/// list a kind alike. What needs OSDU (the kinds a wildcard names, whether storage's admin route answers) is found out when
/// a run or a plan reads it.
/// </summary>
internal static class DownloadMapper
{
    /// <summary>The tokens of a retrieval's location that name a run, which a download's folder never holds: it is the same folder every run.</summary>
    private static readonly string[] RunTokens = ["run", "date"];

    public static DownloadFlowDefinition Map(DownloadFlowYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var target = y.Target ?? throw FlowMapper.Missing("target", source);
        var headers = FlowMapper.MapHeaders(src.Headers, "source.headers", source);
        var partitions = InventoryMapper.MapPartitions(y.Partitions, source);
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

        var parameters = FlowParameterDeclarations.Map(y.Parameters, source);
        if (parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', which a download flow keeps for the partition a run reads: {{partition}} in a query or the location is always that partition. Rename the parameter.");
        }

        var read = InventoryMapper.MapRead(src.Read, source);
        return new DownloadFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            TelemetryOperation = FlowMapper.MapTelemetry(y.Telemetry, DownloadFlowDefinition.FlowTypeName, source),
            Parameters = parameters,
            Source = new InventorySource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
                Read = read,
                QueryPath = InventoryMapper.ServicePath(src.QueryPath, InventorySource.DefaultQueryPath, "source.queryPath", source),
                SearchPath = InventoryMapper.ServicePath(src.SearchPath, InventorySource.DefaultSearchPath, "source.searchPath", source),
                Paging = FlowMapper.ParseEnum(src.Paging, SearchPaging.Cursor, "source.paging", source),
                RecordQueryPath = InventoryMapper.ServicePath(src.RecordQueryPath, InventorySource.DefaultRecordQueryPath, "source.recordQueryPath", source),
                HeadersPath = InventoryMapper.ServicePath(src.HeadersPath, InventorySource.DefaultHeadersPath, "source.headersPath", source),
                SchemaPath = InventoryMapper.ServicePath(src.SchemaPath, InventorySource.DefaultSchemaPath, "source.schemaPath", source),
            },
            Target = new DownloadTarget { Location = MapLocation(target.Location, parameters, source) },
            Partitions = partitions,
            FollowsRegistry = partitions.Count == 0 && !headers.ContainsKey(FlowMapper.PartitionHeader),
            Downloads = MapDownloads(y.Downloads, parameters, read, source),
            Reliability = FlowMapper.MapReliability(y.Reliability, "a download flow", takesRecordsAtOnce: true, source),
            Parallelism = FlowMapper.MapRunParallelism(y.Parallelism, "the storage reads a run has in flight at once", source),
        };
    }

    /// <summary>
    /// The folder the downloads' folders are made in: written out (a <c>${...}</c> reference is never resolved in a location),
    /// its tokens the partition and declared parameters, and absolute, since a run reads its flow from a checkout of its own
    /// and a relative path would land the files there. A location that opens with a token is checked once the run has filled it.
    /// </summary>
    private static string MapLocation(string? declared, IReadOnlyDictionary<string, FlowParameter> parameters, string source)
    {
        var location = FlowMapper.Require(declared, "target.location", source);
        FlowMapper.RefuseLocationReference(location, "target.location", source);
        foreach (var token in FlowMapper.Tokens(location))
        {
            if (RunTokens.Contains(token, StringComparer.Ordinal))
            {
                throw new FlowValidationException(
                    $"{source}: target.location uses '{{{token}}}', and a download writes every run to the same folder, replacing a file when OSDU holds a newer version of its record; a folder per run would hold only what that run wrote. Take the token out.");
            }

            if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: target.location uses '{{{token}}}', which is neither {{{PartitionNames.RunValue}}} nor declared under parameters.");
            }
        }

        if (!location.StartsWith('{') && !IsAbsolute(location))
        {
            throw new FlowValidationException(
                $"{source}: target.location '{location}' is a relative path, and a run reads its flow from a checkout of its own, so the files would land inside that checkout. Write an absolute path or a storage URI (abfss://... or https://<account>.blob.core.windows.net/...).");
        }

        return location;
    }

    /// <summary>Whether a location names where it is without a working directory: a URI, or a rooted local path.</summary>
    internal static bool IsAbsolute(string location)
        => location.Contains("://", StringComparison.Ordinal) || Path.IsPathFullyQualified(location) || location.StartsWith('/');

    private static IReadOnlyList<DownloadSpec> MapDownloads(List<DownloadYaml>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, InventoryRead read, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: downloads must list at least one download; a download flow without downloads has nothing to read.");
        }

        if (declared.Count > DownloadFlowDefinition.MaxDownloads)
        {
            throw new FlowValidationException(
                $"{source}: downloads lists {declared.Count} downloads; one flow holds at most {DownloadFlowDefinition.MaxDownloads}. Split them over several flows.");
        }

        var downloads = new List<DownloadSpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"downloads[{i}]");
            var d = declared[i] ?? throw new FlowValidationException($"{source}: {at} is empty.");
            var name = FlowMapper.Require(d.Name, at + ".name", source);
            if (!SelectableNames.IsName(name))
            {
                throw new FlowValidationException($"{source}: {at}.name '{name}' is not a download name: {SelectableNames.Rule}.");
            }

            if (!IsFolderName(name))
            {
                throw new FlowValidationException(
                    $"{source}: {at}.name '{name}' names the folder the download's files go to, and Windows makes no folder of that name (one ending in '.', or a device name such as CON, NUL, COM1 or LPT1). Name it otherwise.");
            }

            if (downloads.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException(
                    $"{source}: {at} is named '{name}', as an earlier download is; each download writes to the folder of its name, so each needs its own.");
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
                    throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{source}: {where}: query is {query.Length} characters; a download keeps at most {DeliveryModel.MaxInventoryQueryLength}."));
                }

                foreach (var token in FlowMapper.Tokens(query))
                {
                    if (token != PartitionNames.RunValue && !parameters.ContainsKey(token))
                    {
                        throw new FlowValidationException($"{source}: {where}: query uses '{{{token}}}', which is not declared under parameters.");
                    }
                }
            }

            downloads.Add(new DownloadSpec { Name = name, Description = Optional(d.Description), Kind = kind, Query = query });
        }

        return downloads;
    }

    /// <summary>
    /// Whether every file system a download writes to makes a folder named <paramref name="name"/>: Windows drops a trailing
    /// '.', and refuses its device names whatever follows a '.' after them.
    /// </summary>
    internal static bool IsFolderName(string name)
    {
        if (name.EndsWith('.'))
        {
            return false;
        }

        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
