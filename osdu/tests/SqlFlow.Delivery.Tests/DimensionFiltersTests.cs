using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The filter text a dimension's values and keys stand for (osdu/docs/reference/flow/dimension-build.md, What a build
/// does). A build stores it with each key and value, and the API and the CLI build it again for any set of values, so the
/// text itself is pinned here: a change to it is a change to every filter already stored.
/// </summary>
public class DimensionFiltersTests
{
    private static readonly OsduField Mnemonic = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
    private static readonly OsduField WellboreId = OsduField.Keyword("data.WellboreID");

    [Fact]
    public void A_filter_asks_for_each_key_once_in_ordinal_order_the_way_the_index_holds_the_field()
    {
        Assert.Equal(
            ["""(nested(data.Curves, (Mnemonic.keyword:"GR"))) OR (nested(data.Curves, (Mnemonic.keyword:"gr")))"""],
            DimensionFilters.Of(Mnemonic, ["gr", "GR", "GR"]));
        Assert.Equal(
            ["data.WellboreID:\"dev:master-data--Wellbore:b-1:\""],
            DimensionFilters.Of(WellboreId, ["dev:master-data--Wellbore:b-1:"]));
        Assert.Empty(DimensionFilters.Of(WellboreId, []));
    }

    [Fact]
    public void Past_the_keys_one_query_holds_the_filter_is_another_query()
    {
        var keys = Enumerable.Range(0, DimensionFilters.MaxOriginalsPerQuery + 1).Select(i => $"k{i:D4}").ToList();

        var queries = DimensionFilters.Of(WellboreId, keys);

        Assert.Equal(2, queries.Count);
        Assert.StartsWith("""data.WellboreID:("k0000" OR "k0001" OR """, queries[0], StringComparison.Ordinal);
        Assert.EndsWith(""" OR "k0499")""", queries[0], StringComparison.Ordinal);
        Assert.Equal("data.WellboreID:\"k0500\"", queries[1]);
    }

    [Fact]
    public void The_filter_of_none_of_the_keys_starts_from_every_record()
    {
        Assert.Equal(
            """_exists_:id AND NOT (nested(data.Curves, (Mnemonic.keyword:"GR")))""",
            DimensionFilters.NoneOf(Mnemonic, ["GR"]));
        Assert.Equal("_exists_:id", DimensionFilters.NoneOf(Mnemonic, []));
    }

    [Fact]
    public void A_key_no_query_can_carry_is_said_to_be_so_before_a_filter_is_asked()
    {
        Assert.True(DimensionFilters.Filterable(Mnemonic, "GR"));
        Assert.False(DimensionFilters.Filterable(Mnemonic, "null"));
        Assert.Throws<OsduQueryException>(() => DimensionFilters.Of(Mnemonic, ["GR", "null"]));
    }
}
