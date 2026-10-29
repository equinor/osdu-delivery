using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Storage;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A download flow as the platform sees it: the OSDU endpoint is the source reference, the folder its files go to the target
/// reference, and the source's secret references are what the hygiene check inspects. It needs the repository tree for the
/// repository document above it, whose code its requests carry. Its lineage is the kinds it reads and the files it writes
/// (<see cref="DownloadLineage"/>), so it runs after the flows that write those kinds.
/// </summary>
public sealed record DownloadFlowDocument : RegisteredFlowDocument
{
    public required DownloadFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => DownloadFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint;

    public override string? TargetReference => Flow.Target.Location;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    /// <summary>
    /// A run reads the repository document above the flow, whose code its requests carry (osdu/docs/reference/flow/repository.md),
    /// so its node needs the repository's tree at the run's commit: a snapshot of the flow's one file holds none.
    /// </summary>
    public override bool RequiresRepoTree => true;

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OrderLineage.BindEveryPartition(DownloadLineage.Describe(Flow), context);
    }

    /// <summary>The repository document above the flow, found and read as a run finds and reads it (<see cref="CompanionFiles.Repository"/>).</summary>
    public override IReadOnlyList<string> CheckOffline(string documentPath) => CompanionFiles.Repository(documentPath, new DeliveryDocumentLoader());
}

/// <summary>The <c>flowType: download</c> document kind, registered in every host next to its executor.</summary>
public sealed class DownloadFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public DownloadFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => DownloadFlowDefinition.FlowTypeName;

    public string Description => "write every record of OSDU kinds in a partition to a folder, a JSON file each named after its id, writing again only what is new or changed and keeping the version each file holds in a ledger";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Download, "Download", "List every id each download's kind holds (all of them, or those the payload names), and write each record that is new or changed to its file; a forced run writes every one again.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Count the records each download would list, and say where it would write and what would stop it, reading no record and writing nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new DownloadFlowDocument { Flow = _loader.ParseDownload(yaml, source) };
    }

    /// <summary>
    /// What a download flow's run may be given: its parameters, and the partition it reads when it works in partitions (<see cref="KindRunInputs"/>).
    /// </summary>
    public FlowRunInputs DescribeRun(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var flow = _loader.ParseDownload(yaml, source);
        return KindRunInputs.Of(flow.Parameters, flow.Partitioned, "reads");
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseSeveralValues(parameters, DownloadFlowDefinition.FlowTypeName, scopeTakesSeveral: false);
        DeliveryOperations.RefuseBuiltInOverrides(
            parameters, DownloadFlowDefinition.FlowTypeName, "force in the payload writes every record OSDU serves again; name the downloads to run in the payload.");
        var operation = Operation(parameters);

        // A run names the downloads it takes and whether it writes every record again, beside the central configuration.
        var payload = DeliveryRunPayload.Parse(parameters);
        payload.RefuseOtherThan(DownloadFlowDefinition.FlowTypeName, [DeliveryRunPayload.DownloadsProperty, DeliveryRunPayload.ForceProperty]);
        if (payload.Force && operation == DeliveryOperations.Plan)
        {
            throw new SqlFlowException(
                $"payload {DeliveryRunPayload.ForceProperty} does not apply to the {DeliveryOperations.Plan} operation: a plan writes no file, and only a {DeliveryOperations.Download} run writes every record again.");
        }
    }

    /// <summary>The operation a download flow runs: download (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Download) switch
        {
            DeliveryOperations.Download => DeliveryOperations.Download,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A download flow runs the {DeliveryOperations.Download} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }
}

/// <summary>
/// What a download flow contributes to SQLFlow's lineage (osdu/docs/reference/concepts/lineage.md, What each kind reads and
/// writes): it reads the kind (wildcards allowed) of each download on its platform and partition, and writes a JSON file per
/// record into each download's folder. It is so ordered after the delivery flows writing the kinds it reads, and a file flow
/// reading a download's folder is ordered after it, as after any flow landing files.
/// </summary>
public static class DownloadLineage
{
    /// <summary>The file name glob of the files a run writes (<c>dev~master-data--Wellbore~WB-1.json</c>).</summary>
    public const string RecordFiles = "*.json";

    /// <summary>
    /// Everything the flow contributes, the kinds once each in declaration order, then each download's files. A flow that names
    /// its partitions reads in every one of them; one that leaves them to the registry is described once, under the partition
    /// that stands for every registered one.
    /// </summary>
    public static RegisteredFlowLineage Describe(DownloadFlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"download flow '{flow.Name}'";
        var warnings = new List<string>();
        var partitions = flow.DeclaresPartitions && flow.Partition is null
            ? flow.Partitions
            : flow.FollowsRegistry && flow.Partition is null
                ? [PartitionNames.Every]
                : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings) is { } one ? [one] : [];

        var datasets = new List<DeclaredDataset>();
        foreach (var partition in partitions)
        {
            foreach (var download in flow.Downloads)
            {
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, download.Kind, who, $"download '{download.Name}'", warnings) is { } read
                    && !datasets.Contains(read))
                {
                    datasets.Add(read);
                }
            }
        }

        var location = flow.Target.Location.Trim();
        var files = flow.Downloads
            .Select(d => new DeclaredFileLocation { Relation = LineageRelation.Writes, Location = FileStoreRegistry.Join(location, d.Name), FilePattern = RecordFiles })
            .ToList();
        return new RegisteredFlowLineage { Files = files, Datasets = datasets, Warnings = warnings };
    }
}
