using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The run parameters suite: every rule of a flow document's <c>parameters</c> for each of the module's seven flow kinds
/// (its choices, a delivery flow's lookups), the one rule a run resolves its values by checked against what a run form and
/// a schedule's preview explain for the same document and values, the first records of a delivery's scope as a run
/// setting over every operation and payload, and a mapping's parameters (osdu/docs/reference/flow/parameters.md). It is
/// a suite of its own, run on demand (<c>bash tools/test.sh --suite RunParameters</c>, CONTRIBUTING.md, Tests): the unit
/// and full tiers leave out every test that carries its trait, <c>[Trait("Suite", "RunParameters")]</c>. A class of it
/// that needs SQL Server is an integration test of the <see cref="SqlServerSuite"/> as well, as every other is.
/// </summary>
public static class RunParameterSuite
{
    /// <summary>The trait an on-demand suite is named by, beside <see cref="TestTierTests.Category"/>.</summary>
    public const string Trait = "Suite";

    /// <summary>The value of <see cref="Trait"/> for this suite.</summary>
    public const string Name = "RunParameters";
}

/// <summary>
/// A document of each of the module's flow kinds, small enough to read and complete enough to load, whose
/// <c>parameters</c> a test writes (<see cref="Document"/>): what the suite holds every kind to the same rules with. A
/// delivery flow's record scope binds <c>logSource</c>, which it always declares, required.
/// </summary>
public sealed record FlowKindDocument(string FlowType, string Template, Func<DeliveryDocumentLoader, IFlowDocumentKind> Kind)
{
    /// <summary>The marker <see cref="Template"/> holds where the test's parameter entries go, each a line under <c>parameters:</c>.</summary>
    private const string Slot = "{parameters}";

    /// <summary>Every kind's document, in the order the reference lists the kinds.</summary>
    public static IReadOnlyList<FlowKindDocument> All { get; } =
    [
        new(FlowDefinition.FlowTypeName, """
            flowType: delivery
            name: logs
            parameters:
              logSource: { required: true }
            {parameters}source:
              connection: ${env:OSDU_DATA_DB}
              record:
                object: OsduData.silver.WellLog
                key: [source_project, log_id]
                scope: { log_source: logSource }
              work: work/{logSource}
            render:
              mapping: WellLog@1.4.0
              parameters: { dataPartition: dev }
            target:
              endpoint: https://example.org/facade-api
              headers: { data-partition-id: dev }
              protocol: storage
            """, loader => new DeliveryFlowKind(loader)),
        new(RetrievalDefinition.FlowTypeName, """
            flowType: retrieval
            name: wellbores-out
            parameters:
            {parameters}source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
              kind: "osdu:wks:master-data--Wellbore:1.0.0"
            target:
              location: ./out
            """, loader => new RetrievalFlowKind(loader)),
        new(CacheDefinition.FlowTypeName, """
            flowType: cache
            name: reference-cache
            parameters:
            {parameters}source:
              endpoint: ${env:OSDU_URL}
              headers:
                data-partition-id: dev
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code]
            """, loader => new CacheFlowKind(loader)),
        new(AssertionFlowDefinition.FlowTypeName, """
            flowType: assertion
            name: checks
            partitions: [dev]
            parameters:
            {parameters}source: { endpoint: http://localhost }
            tests:
              - name: one
                kind: "osdu:wks:master-data--Wellbore:1.3.0"
                assert:
                  - count: { atLeast: 1 }
            """, loader => new AssertionFlowKind(loader)),
        new(DimensionFlowDefinition.FlowTypeName, """
            flowType: dimension
            name: dimensions
            parameters:
            {parameters}source:
              endpoint: http://localhost
            dimensions:
              - name: LogSource
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Source
            """, loader => new DimensionFlowKind(loader)),
        new(InventoryFlowDefinition.FlowTypeName, """
            flowType: inventory
            name: inventory
            partitions: [dev, test]
            parameters:
            {parameters}source:
              endpoint: ${env:OSDU_URL}
              auth:
                type: bearer
                secretRef: ${env:OSDU_TOKEN}
            inventories:
              - name: WellLogs
                kind: "*:*:work-product-component--WellLog:*"
            """, loader => new InventoryFlowKind(loader)),
        new(RemovalFlowDefinition.FlowTypeName, """
            flowType: removal
            name: removal
            partitions: [dev, test]
            parameters:
            {parameters}removals:
              - flow: welldb-wellbore-03-delivery
                keys:
                  type: csv
                  location: ../data/wellbore
                  options: { delimiter: ",", header: true }
            """, loader => new RemovalFlowKind(loader)),
    ];

    /// <summary>The kind named <paramref name="flowType"/>.</summary>
    public static FlowKindDocument Of(string flowType) => All.Single(k => k.FlowType == flowType);

    /// <summary>The flow types, as theory data.</summary>
    public static TheoryData<string> FlowTypes()
    {
        var data = new TheoryData<string>();
        foreach (var kind in All)
        {
            data.Add(kind.FlowType);
        }

        return data;
    }

    /// <summary>True for the delivery kind, whose scope parameter takes several values and whose parameters a lookup offers.</summary>
    public bool IsDelivery => FlowType == FlowDefinition.FlowTypeName;

    /// <summary>The document with <paramref name="entries"/> under <c>parameters</c>, each a line such as <c>region: { default: north }</c>.</summary>
    public string Document(params string[] entries)
        => Template.ReplaceLineEndings("\n").Replace(Slot, string.Concat(entries.Select(entry => "  " + entry + "\n")), StringComparison.Ordinal);

    /// <summary>
    /// The parameters a run of the document resolves, exactly as the kind's executor resolves them: a delivery flow's with
    /// the parameters its scope binds taking several values, every other kind's taking one each.
    /// </summary>
    public IReadOnlyDictionary<string, string> Resolve(DeliveryDocumentLoader loader, string yaml, IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentNullException.ThrowIfNull(loader);
        const string Source = "flows/suite.yaml";
        return FlowType switch
        {
            FlowDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseFlow(yaml, Source), supplied),
            RetrievalDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseRetrieval(yaml, Source).Parameters, Source, supplied),
            CacheDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseCache(yaml, Source).Parameters, Source, supplied),
            AssertionFlowDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseAssertion(yaml, Source).Parameters, Source, supplied),
            DimensionFlowDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseDimension(yaml, Source).Parameters, Source, supplied),
            InventoryFlowDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseInventory(yaml, Source).Parameters, Source, supplied),
            RemovalFlowDefinition.FlowTypeName => FlowParameters.Resolve(loader.ParseRemoval(yaml, Source).Parameters, Source, supplied),
            _ => throw new InvalidOperationException($"The suite has no document of kind '{FlowType}'."),
        };
    }

    /// <summary>The parameters the document declares, as the kind reads them.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters(DeliveryDocumentLoader loader, string yaml)
    {
        ArgumentNullException.ThrowIfNull(loader);
        const string Source = "flows/suite.yaml";
        return FlowType switch
        {
            FlowDefinition.FlowTypeName => loader.ParseSource(yaml, Source).Parameters,
            RetrievalDefinition.FlowTypeName => loader.ParseRetrieval(yaml, Source).Parameters,
            CacheDefinition.FlowTypeName => loader.ParseCache(yaml, Source).Parameters,
            AssertionFlowDefinition.FlowTypeName => loader.ParseAssertion(yaml, Source).Parameters,
            DimensionFlowDefinition.FlowTypeName => loader.ParseDimension(yaml, Source).Parameters,
            InventoryFlowDefinition.FlowTypeName => loader.ParseInventory(yaml, Source).Parameters,
            RemovalFlowDefinition.FlowTypeName => loader.ParseRemoval(yaml, Source).Parameters,
            _ => throw new InvalidOperationException($"The suite has no document of kind '{FlowType}'."),
        };
    }
}
