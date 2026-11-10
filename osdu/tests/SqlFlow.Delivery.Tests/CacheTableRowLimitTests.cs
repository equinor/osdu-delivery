using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How many rows a table type of a cache flow captures (<c>types[].maxRows</c>, osdu/docs/reference/flow/cache-origins.md, A
/// table an ingestion flow loads): 1,000,000 unless the type says otherwise, at most 5,000,000, and only on a type read from
/// an ingestion table. A table holding more is refused whole, with a message naming the number and how to raise it.
/// </summary>
public sealed class CacheTableRowLimitTests
{
    private const string Tables = """
        flowType: cache
        name: lookups
        source:
          connection: ${env:OSDU_DATA_DB}
          headers: { data-partition-id: dev }
        types:
          - table: OsduData.silver.UnitAlias
            key: source_unit
            fields: [osdu_unit]
        """;

    private static readonly DeliveryDocumentLoader Loader = new();

    private static CacheDefinition Load(string yaml) => Loader.ParseCache(yaml, "cache/lookups.yaml");

    private static string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Load(yaml)).Message;

    /// <summary>The table flow with its type's maxRows written as <paramref name="value"/>.</summary>
    private static string WithMaxRows(string value) => Tables + "\n    maxRows: " + value;

    /// <summary>A flow of one type of another origin (its YAML list item indented by two spaces), with <c>maxRows: 1000</c> on it.</summary>
    private static string OtherWithMaxRows(string type, bool searched = false)
        => "flowType: cache\nname: other\nsource:\n" + (searched ? "  endpoint: https://osdu.example.com\n" : string.Empty)
           + "  headers: { data-partition-id: dev }\ntypes:\n" + type + "\n    maxRows: 1000\n";

    [Fact]
    public void A_table_type_takes_a_million_rows_unless_it_says_otherwise()
    {
        var plain = Assert.Single(Load(Tables).Types);
        Assert.Null(plain.MaxRows);
        Assert.Equal(1_000_000, plain.RowLimit);
        Assert.Equal(LookupKeys.DefaultTableRows, plain.RowLimit);

        var raised = Assert.Single(Load(WithMaxRows("250000")).Types);
        Assert.Equal((250_000, 250_000), (raised.MaxRows, raised.RowLimit));

        Assert.Equal(1, Assert.Single(Load(WithMaxRows("1")).Types).RowLimit);
        Assert.Equal(5_000_000, Assert.Single(Load(WithMaxRows("5000000")).Types).RowLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("5000001")]
    public void A_maxRows_outside_one_to_five_million_is_refused(string value)
    {
        Assert.Contains(
            $"cache/lookups.yaml: types[0]: Cached type 'UnitAlias' says maxRows {value}, and it is the most rows of OsduData.silver.UnitAlias the cache holds: a whole number from 1 to 5000000. Leave it out to hold up to 1000000.",
            Refused(WithMaxRows(value)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_maxRows_that_is_not_a_whole_number_is_refused_at_its_line()
    {
        Assert.Contains("cache/lookups.yaml: invalid YAML at line 10", Refused(WithMaxRows("lots")), StringComparison.Ordinal);
        Assert.Contains("cache/lookups.yaml: invalid YAML at line 10", Refused(WithMaxRows("2.5")), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_type_read_from_an_ingestion_table_takes_maxRows()
    {
        Assert.Contains(
            "types[0]: Cached type 'UnitOfMeasure' comes from kind osdu:wks:reference-data--UnitOfMeasure:*, which takes no 'maxRows'",
            Refused(OtherWithMaxRows(
                """
                  - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                    fields: [data.Code]
                """,
                searched: true)),
            StringComparison.Ordinal);
        Assert.Contains(
            "types[0]: Cached type 'sampling-domain' comes from dictionary sampling-domain, which takes no 'maxRows'",
            Refused(OtherWithMaxRows(
                """
                  - dictionary: sampling-domain
                """)),
            StringComparison.Ordinal);
        Assert.Contains(
            "types[0]: Cached type 'Field' comes from dimension Field of welldb-welllog-05-dimensions, which takes no 'maxRows'",
            Refused(OtherWithMaxRows(
                """
                  - dimension: Field
                    dimensionFlow: welldb-welllog-05-dimensions
                """)),
            StringComparison.Ordinal);

        // The spec refuses it too, whoever builds one.
        var spec = new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure", EntityType = "reference-data--UnitOfMeasure", Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
            Fields = [new ReferenceFieldSpec("data.Code")], MaxRows = 10,
        };
        Assert.Contains("which takes no 'maxRows'", Assert.Throws<FlowValidationException>(spec.Validate).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_over_its_rows_is_refused_naming_the_number_it_takes_and_how_far_maxRows_goes()
    {
        var flow = Load(Tables);
        Assert.Equal(
            "Cache flow 'lookups': table OsduData.silver.UnitAlias holds more than 1000000 rows for type UnitAlias, the 1000000 a table type holds when its maxRows says nothing: every row is held in memory with the cache version a render reads. Raise the type's maxRows (at most 5000000) if the table really holds that many, or join it into the delivery flow's reads instead (source.datasets.<name>.lookups), where SQL Server joins it and nothing holds it in memory. Nothing was captured.",
            TableCapture.TooManyRows(flow, Assert.Single(flow.Types)));

        var raised = Load(WithMaxRows("250000"));
        Assert.Contains(
            "holds more than 250000 rows for type UnitAlias, its maxRows: every row",
            TableCapture.TooManyRows(raised, Assert.Single(raised.Types)),
            StringComparison.Ordinal);
    }
}
