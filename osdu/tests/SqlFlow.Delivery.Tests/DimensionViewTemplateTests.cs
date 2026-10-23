using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The flow's templates as a view's joins are checked and offered by (osdu/docs/reference/flow/dimension-views.md, What
/// the templates say of a view's joins): the template of the records a joined column is read from says which records it
/// names, so a join to a dimension of another entity type is marked, and the joins a view could make are offered,
/// through a joined dimension too. The sample WellLog 1.4.0 template names a curve's unit a unit of measure and a log's
/// sampling domain a well log sampling domain type.
/// </summary>
[Trait("Category", "Integration")]
public class DimensionViewTemplateTests
{
    private const string Flow = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: ${env:OSDU_URL}
        target:
          connection: ${env:SQLFLOW_CATALOG_DB}
        dimensions:
          - name: LogCurve
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            path: id
            columns: { key: WellLogID, value: WellLogName }
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                CurveUnitID: { path: CurveUnit, keep: id }
          - name: WellLog
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            path: id
            columns: { key: WellLogID, value: LogName }
            attributes:
              SamplingDomainTypeID: { path: data.SamplingDomainTypeID, keep: id }
          - name: Unit
            kind: "*:*:reference-data--UnitOfMeasure:*"
            path: id
            columns: { key: UnitID, value: UnitCode }
          - name: Domain
            kind: "*:*:reference-data--WellLogSamplingDomainType:*"
            path: id
            columns: { key: DomainID, value: DomainName }
          - name: AnyRecord
            kind: "*:*:*:*"
            path: id
            columns: { key: RecordID, value: RecordName }

        """;

    private static DimensionFlowDefinition Parse(string views) => new DeliveryDocumentLoader().ParseDimension(Flow + views, "flows/dims.yaml");

    [Fact]
    public async Task A_join_the_template_names_agrees_one_to_another_type_differs_and_one_no_template_describes_is_unchecked()
    {
        var flow = Parse("""
            views:
              - name: Curve
                from: LogCurve
                join:
                  - { on: WellLogID, to: WellLog, as: Log }
                  - { on: CurveUnitID, to: Unit }
                  - { on: Log.SamplingDomainTypeID, to: Domain }
                  - { on: CurveUnitID, to: Domain, as: WrongDomain }
                  - { on: CurveUnitID, to: AnyRecord }
            """);

        var verdicts = await DimensionViewTemplates.CheckAsync(flow, flow.Views[0], Samples.SampleTemplates, CancellationToken.None);

        Assert.Equal(
            [
                ("Log", DimensionViewTemplates.Agrees), ("Unit", DimensionViewTemplates.Agrees), ("Domain", DimensionViewTemplates.Agrees),
                ("WrongDomain", DimensionViewTemplates.Differs), ("AnyRecord", DimensionViewTemplates.Unchecked),
            ],
            verdicts.Select(v => (v.Alias, v.Verdict)));
        Assert.Equal(["reference-data--UnitOfMeasure"], verdicts[1].Names);
        Assert.Equal("reference-data--WellLogSamplingDomainType", verdicts[2].Reads);
        Assert.Contains("the join finds nothing", verdicts[3].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_joins_a_view_could_make_are_offered_through_a_joined_dimension_too_and_none_to_a_record_of_any_type()
    {
        var flow = Parse(string.Empty);

        var offered = await DimensionViewTemplates.SuggestAsync(flow, "LogCurve", Samples.SampleTemplates, CancellationToken.None);

        Assert.Equal(
            [("WellLogID", "WellLog", "WellLog"), ("CurveUnitID", "Unit", "Unit"), ("WellLog.SamplingDomainTypeID", "Domain", "Domain")],
            offered.Select(j => (j.On, j.To, j.As)));
        Assert.All(offered, j => Assert.False(string.IsNullOrWhiteSpace(j.Note)));

        // What is offered is what a document can declare: the same joins load.
        var declared = Parse("views:\n  - name: Offered\n    from: LogCurve\n    join:\n" + string.Join("\n", offered.Select(j => $"      - {{ on: {j.On}, to: {j.To}, as: {j.As} }}")));
        Assert.Equal(3, declared.Views[0].Joins.Count);
    }
}
