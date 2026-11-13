using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a dimension flow contributes to SQLFlow's lineage (osdu/docs/reference/concepts/lineage.md, What each kind reads and
/// writes): it reads the kind (wildcards allowed) of each of its dimensions on its platform and partition, and the
/// dictionary file each map step names, and writes each dimension in its partition. A dimension flow is so ordered after the
/// delivery flows writing the kinds it reads, and a cache flow holding one of its dimensions after it. A flow naming its
/// <c>target.connection</c> also writes each dimension's table and each of its views on that connection, so a pipeline
/// reading them is ordered after it.
/// </summary>
public static class DimensionLineage
{
    /// <summary>
    /// Everything the flow contributes, in declaration order. A flow that names its partitions contributes for every one of
    /// them; one that leaves them to the registry is described once, under the partition that stands for every registered one.
    /// A dictionary's file is found as a build finds it, inside the estate <paramref name="context"/> scans.
    /// </summary>
    public static RegisteredFlowLineage Describe(DimensionFlowDefinition flow, RegisteredLineageContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"dimension flow '{flow.Name}'";
        var warnings = new List<string>();
        var partitions = flow.DeclaresPartitions && flow.Partition is null
            ? flow.Partitions
            : flow.FollowsRegistry && flow.Partition is null
                ? [PartitionNames.Every]
                : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings) is { } one ? [one] : [];

        var datasets = new List<DeclaredDataset>();
        foreach (var partition in partitions)
        {
            foreach (var dimension in flow.Dimensions.Where(d => d.BuildsIn(partition == PartitionNames.Every ? null : partition)))
            {
                var what = $"dimension '{dimension.Name}'";
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, dimension.Kind, who, what, warnings) is { } read
                    && !datasets.Contains(read))
                {
                    datasets.Add(read);
                }

                if (OsduLineage.Dimension(LineageRelation.Writes, partition, flow.Name, dimension.Name, who, warnings) is { } write && !datasets.Contains(write))
                {
                    datasets.Add(write);
                }
            }
        }

        var files = new List<DeclaredFileLocation>();
        if (context is not null)
        {
            foreach (var name in flow.Dimensions.SelectMany(d => d.Clean).Where(s => s.Kind == CleanStepKind.Map).Select(s => s.Dictionary!).Distinct(StringComparer.Ordinal))
            {
                if (CacheLineage.DictionaryFile(name, context) is { } location)
                {
                    files.Add(new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = location });
                }
                else
                {
                    warnings.Add($"{who}: a map step names dictionary {name}, which was not found in a {DictionaryCatalog.DirectoryName}/ directory above the flow, so its lineage leaves the file out.");
                }
            }
        }

        return new RegisteredFlowLineage { Objects = Objects(flow), Datasets = datasets, Files = files, Warnings = warnings };
    }

    /// <summary>
    /// The database objects the flow writes (osdu/docs/reference/flow/dimension-target.md): each dimension's table and each
    /// view, in the target's database and schema, on the connection the target names (the module's own when it names a
    /// database or a schema alone), so a pipeline reading either through the same reference is ordered after the flow. A
    /// flow naming no target declares none.
    /// </summary>
    private static List<DeclaredDataObject> Objects(DimensionFlowDefinition flow)
    {
        if (flow.Target is not { } target)
        {
            return [];
        }

        var objects = flow.Dimensions
            .Select(d => DimensionTables.NameOf(d.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(table => new DeclaredDataObject
            {
                Relation = LineageRelation.Writes,
                ConnectionReference = target.ConnectionReference,
                Database = target.Database,
                Schema = target.Schema ?? DeliveryModel.SchemaName,
                Name = table,
                Kind = LineageNodeKind.Table,
            })
            .ToList();
        objects.AddRange(flow.Views.Select(view => new DeclaredDataObject
        {
            Relation = LineageRelation.Writes,
            ConnectionReference = target.ConnectionReference,
            Database = target.Database,
            Schema = target.Schema ?? DeliveryModel.SchemaName,
            Name = view.ViewName,
            Kind = LineageNodeKind.View,
        }));
        return objects;
    }
}
