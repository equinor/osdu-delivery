using System.Text.Json.Nodes;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The rules of a match that need no database (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds): which
/// way it reads OSDU, how many ids one search names beside the conditions, which properties its rows carry and how much of
/// what an id holds there it keeps, and how it counts the searches it sends.
/// </summary>
public sealed class InventorySelectionTests
{
    [Theory]
    [InlineData(200, 60_000, InventorySelections.ByConditions)]
    [InlineData(60_000, 60_000, InventorySelections.ByConditions)]
    [InlineData(0, 60_000, InventorySelections.ByConditions)]
    [InlineData(1_500_000, 60_000, InventorySelections.ByIds)]
    [InlineData(4_200, 1_200, InventorySelections.ByIds)]
    [InlineData(3_000, 1_500, InventorySelections.ByConditions)]
    public void A_match_reads_whichever_way_sends_fewer_searches_the_records_first_when_both_send_as_many(long found, long candidates, string direction)
        => Assert.Equal(direction, InventorySelections.Direction(found, candidates, OsduSearch.MaxPage, InventorySelections.IdsPerQuery));

    [Fact]
    public void One_search_names_as_many_ids_as_the_clause_limit_leaves_beside_the_conditions_within_its_bounds()
    {
        Assert.Equal(InventorySelections.IdsPerQuery, InventorySelections.IdsPerSearch(1));
        Assert.Equal(400, InventorySelections.IdsPerSearch(600));
        Assert.Equal(InventorySelections.MinIdsPerQuery, InventorySelections.IdsPerSearch(5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventorySelections.IdsPerSearch(-1));

        // The clauses conditions take: one each, or one per value an anyOf or a noneOf compares.
        IReadOnlyList<ExplorerFilter> filters =
        [
            new() { Path = "data.FacilityName", Condition = ExplorerCondition.Contains, Value = "A-1" },
            new() { Path = "data.Source", Index = OsduFieldIndex.Keyword, Condition = ExplorerCondition.AnyOf, Values = ["a", "b", "c"] },
            new() { Path = "data.Field", Index = OsduFieldIndex.Keyword, Condition = ExplorerCondition.NoneOf, Values = ["x", "y"] },
        ];
        Assert.Equal(6, InventoryRunner.Clauses(filters));
    }

    [Fact]
    public void A_selection_s_rows_carry_the_properties_its_conditions_ask_each_once_but_the_id_and_the_kind()
    {
        IReadOnlyList<ExplorerFilter> filters =
        [
            new() { Path = "data.FacilityName", Value = "A" },
            new() { Path = "kind", Index = OsduFieldIndex.Keyword, Value = "osdu:wks:x:1.0.0" },
            new() { Path = "data.FacilityName", Condition = ExplorerCondition.Exists },
            new() { Path = "id", Index = OsduFieldIndex.Keyword, Value = "dev:x:1" },
            new() { Path = "data.GeoContexts.FieldID", Index = OsduFieldIndex.Keyword, Nested = "data.GeoContexts", Value = "dev:master-data--Field:F:" },
        ];

        Assert.Equal(["data.FacilityName", "data.GeoContexts.FieldID"], InventoryRunner.SelectionColumns(filters));
    }

    [Fact]
    public void What_an_id_holds_is_kept_whole_when_it_fits_and_cut_from_the_end_of_its_longest_list_when_it_does_not()
    {
        var hit = new JsonObject
        {
            ["id"] = "dev:wpc--WellLog:a",
            ["data"] = new JsonObject
            {
                ["FacilityName"] = "Wellbore A-1",
                ["GeoContexts"] = new JsonArray(new JsonObject { ["FieldID"] = "F1" }, new JsonObject { ["FieldID"] = "F2" }),
            },
        };

        var kept = InventoryRunner.SelectionValues(hit, ["data.FacilityName", "data.GeoContexts.FieldID", "data.Missing"]);
        var held = InventorySelectionValues.Parse(kept);
        Assert.Equal(["Wellbore A-1"], held["data.FacilityName"]);
        Assert.Equal(["F1", "F2"], held["data.GeoContexts.FieldID"]);
        Assert.False(held.ContainsKey("data.Missing"));
        Assert.Null(InventoryRunner.SelectionValues(hit, ["data.Missing"]));

        // Twenty values of 250 characters each outgrow the column: the last ones go first, and what is kept still parses.
        var many = new JsonArray(Enumerable.Range(0, 20).Select(i => (JsonNode?)new JsonObject { ["Name"] = new string((char)('a' + i), 250) }).ToArray());
        var wide = new JsonObject { ["data"] = new JsonObject { ["Logs"] = many, ["FacilityName"] = "W" } };
        var cut = InventoryRunner.SelectionValues(wide, ["data.Logs.Name", "data.FacilityName"]);
        Assert.NotNull(cut);
        Assert.True(cut.Length <= DeliveryModel.MaxSelectionValuesLength);
        var left = InventorySelectionValues.Parse(cut);
        Assert.InRange(left["data.Logs.Name"].Count, 1, 19);
        Assert.Equal(new string('a', 250), left["data.Logs.Name"][0]);
        Assert.Equal(["W"], left["data.FacilityName"]);

        Assert.Empty(InventorySelectionValues.Parse(null));
        Assert.Throws<DeliveryException>(() => InventorySelectionValues.Parse("[1, 2]"));
    }

    [Fact]
    public void A_match_counts_each_search_request_once_whatever_its_attempts_and_hands_every_call_to_the_run_s_trace()
    {
        var trace = new RecordingObserver();
        var counter = new SearchRequestCounter(new InventorySource { Endpoint = "https://osdu.example.test" }, trace);
        counter.Ended(new HttpAttempt("POST", "https://osdu.example.test/api/search/v2/query", 1, 200, null, TimeSpan.Zero));
        counter.Ended(new HttpAttempt("POST", "https://osdu.example.test/api/search/v2/query_with_cursor", 1, 429, null, TimeSpan.Zero));
        counter.Retrying(new HttpRetry("POST", "https://osdu.example.test/api/search/v2/query_with_cursor", 1, 3, "429", TimeSpan.Zero));
        counter.Ended(new HttpAttempt("POST", "https://osdu.example.test/api/search/v2/query_with_cursor", 2, 200, null, TimeSpan.Zero));
        counter.Ended(new HttpAttempt("DELETE", "https://osdu.example.test/api/search/v2/query_with_cursor/c-1", 1, 200, null, TimeSpan.Zero));
        counter.Ended(new HttpAttempt("POST", "https://login.example.test/oauth2/token", 1, 200, null, TimeSpan.Zero));
        counter.Ended(new HttpAttempt("POST", "https://osdu.example.test/api/storage/v2/query/records", 1, 200, null, TimeSpan.Zero));

        Assert.Equal(3L, counter.Sent);
        Assert.Equal(3L, counter.Take());
        Assert.Equal(0L, counter.Take());
        counter.Ended(new HttpAttempt("POST", "/api/search/v2/query", 1, 200, null, TimeSpan.Zero));
        Assert.Equal(1L, counter.Take());
        Assert.Equal((7, 1), (trace.Attempts, trace.Retries));
    }

    private sealed class RecordingObserver : IHttpObserver
    {
        public int Attempts { get; private set; }

        public int Retries { get; private set; }

        public void Ended(HttpAttempt attempt) => Attempts++;

        public void Retrying(HttpRetry retry) => Retries++;
    }
}
