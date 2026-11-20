using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The counts a list of runs takes from each run's result: the whole-number fields at the top of the result object, under
/// the names the result gives them, and nothing else; no counts at all for a run without a result, or with one that is
/// not a JSON object.
/// </summary>
public sealed class RunResultCountsTests
{
    [Fact]
    public void A_result_gives_the_whole_numbers_at_its_top_under_their_own_names()
    {
        var counts = RunResultCounts.Of(
            """{"operation":"deliver","planned":12,"delivered":9,"held":0,"failed":3,"waiting":0,"seconds":1.5,"nothingToDo":false,"submission":{"delivered":40},"issues":["one"],"error":null}""");

        Assert.NotNull(counts);
        Assert.Equal(
            new Dictionary<string, long> { ["planned"] = 12, ["delivered"] = 9, ["held"] = 0, ["failed"] = 3, ["waiting"] = 0 },
            counts);
    }

    [Fact]
    public void A_number_too_large_or_written_with_a_fraction_is_no_count()
    {
        var counts = RunResultCounts.Of("""{"rows":9223372036854775807,"over":9223372036854775808,"whole":2.0,"exponent":3e2}""");

        Assert.NotNull(counts);
        Assert.Equal(long.MaxValue, counts["rows"]);
        Assert.False(counts.ContainsKey("over"));
        Assert.False(counts.ContainsKey("whole"));
        Assert.False(counts.ContainsKey("exponent"));
    }

    [Fact]
    public void A_name_keeps_its_case_and_its_first_value()
    {
        var counts = RunResultCounts.Of("""{"Delivered":1,"delivered":2,"held":5,"held":6}""");

        Assert.NotNull(counts);
        Assert.Equal(1, counts["Delivered"]);
        Assert.Equal(2, counts["delivered"]);
        Assert.Equal(5, counts["held"]);
    }

    [Fact]
    public void A_result_that_reports_no_whole_number_gives_no_counts_but_is_still_a_result()
    {
        var counts = RunResultCounts.Of("""{"operation":"plan","issues":[]}""");

        Assert.NotNull(counts);
        Assert.Empty(counts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"delivered\"")]
    [InlineData("null")]
    [InlineData("{\"delivered\":9")]
    [InlineData("not json")]
    public void No_result_or_one_that_is_not_an_object_gives_no_counts(string? resultJson)
    {
        Assert.Null(RunResultCounts.Of(resultJson));
    }
}
