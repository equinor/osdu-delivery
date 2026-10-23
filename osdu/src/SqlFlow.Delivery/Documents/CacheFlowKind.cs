using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A cache flow as the platform sees it: the OSDU endpoint (or, for a flow of table types, the connection) is the source
/// reference, the catalog the target, and the source's secret references are what the hygiene check inspects. It needs the
/// repository tree only when it holds a dictionary, whose file a refresh reads. Its lineage is the OSDU types, tables and
/// dictionary files it reads and the partition cache types it writes (<see cref="CacheLineage"/>).
/// </summary>
public sealed record CacheFlowDocument : RegisteredFlowDocument
{
    /// <summary>What the pipeline row shows as a cache flow's target: the versions a refresh writes live in the module database.</summary>
    public const string CatalogTarget = "catalog";

    /// <summary>What the offline check reads dictionaries with. The loader holds no state, so one serves every document.</summary>
    private static readonly DeliveryDocumentLoader DictionaryDocuments = new();

    public required CacheDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => CacheDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    public override string? SourceReference => Flow.Source.Endpoint ?? Flow.Source.Connection;

    /// <summary>The ingestion database the flow's table types are read from, or null when it declares none.</summary>
    public override string? SourceConnectionReference => Flow.Source.Connection;

    /// <summary>The ingestion tables the flow's table types read, so SQLFlow orders the ingestion flows that load them first.</summary>
    public override IReadOnlyList<DeclaredDataObject> DeclaredObjects => CacheLineage.DeclaredObjects(Flow);

    public override string? TargetReference => CatalogTarget;

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    /// <summary>
    /// A flow holding a dictionary reads the dictionary's file from the repository, so its node needs the repository's tree
    /// at the run's commit; a flow of OSDU and table types has everything it needs in the document.
    /// </summary>
    public override bool RequiresRepoTree => Flow.Types.Any(t => t.Origin == Snapshots.CacheOrigin.Dictionary);

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OrderLineage.BindEveryPartition(CacheLineage.Describe(Flow, context), context);
    }

    /// <summary>The dictionary each dictionary type holds, found and read as a refresh finds and reads it (<see cref="CompanionFiles.Dictionaries(CacheDefinition, string, DeliveryDocumentLoader)"/>).</summary>
    public override IReadOnlyList<string> CheckOffline(string documentPath) => CompanionFiles.Dictionaries(Flow, documentPath, DictionaryDocuments);
}

/// <summary>The <c>flowType: cache</c> document kind, registered in every host next to its executor.</summary>
public sealed class CacheFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public CacheFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => CacheDefinition.FlowTypeName;

    public string Description => "capture OSDU reference data, dictionaries and ingestion tables into a versioned cache in the module database, which delivery flows render against";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Refresh, "Refresh", "Capture every declared type, from OSDU, a dictionary or an ingestion table, and merge it into the partition's cache, writing a version when the content moved.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Count what each declared type would hold (the records an OSDU search matches, the entries of a dictionary, the rows of a table), writing nothing.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new CacheFlowDocument { Flow = _loader.ParseCache(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseSeveralValues(parameters, CacheDefinition.FlowTypeName, scopeTakesSeveral: false);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, CacheDefinition.FlowTypeName, "a refresh sweeps every declared type in full.");

        // The control plane supplies its central configuration to every run of the module's kinds in the payload; a cache
        // run takes that and the types it refreshes or plans, since a refresh has no submission, record or slice to name. A
        // run names its partition and the flow's parameter values under values.
        DeliveryRunPayload.Parse(parameters).RefuseOtherThan(CacheDefinition.FlowTypeName, CacheExecutor.PayloadTakes);
    }
}
