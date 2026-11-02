using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The predicate a flow appends to the reads of a table it declares (<see cref="SourceFilter"/>): one predicate with its own
/// leading AND, which cannot end the statement, hide the rest of it, or close a parenthesis it did not open.
/// </summary>
public sealed class SourceFilterTests
{
    [Theory]
    [InlineData("AND DeletedDate_DW IS NULL")]
    [InlineData("  and [status] <> 'draft'")]
    [InlineData("AND (status = 'a;b' OR name = 'it''s -- not a comment')")]
    [InlineData("AND [odd]]name] = 1 AND EXISTS (SELECT 1 FROM OsduData.silver.Region g WHERE g.region_id = region_id)")]
    [InlineData("AND\nDeletedDate_DW IS NULL")]
    public void A_predicate_with_its_own_leading_and_is_accepted(string filter)
        => Assert.Null(SourceFilter.Problem(filter));

    [Theory]
    [InlineData("DeletedDate_DW IS NULL", "carries its own leading AND")]
    [InlineData("ANDstatus = 1", "carries its own leading AND")]
    [InlineData("AND 1 = 1; DROP TABLE x", "holds a ';'")]
    [InlineData("AND 1 = 1 -- the rest", "holds a comment")]
    [InlineData("AND 1 = 1 /* the rest */", "holds a comment")]
    [InlineData("AND 1 = 1) r CROSS JOIN (SELECT 1 x", "closes a parenthesis at character 10")]
    [InlineData("AND (1 = 1", "opens a parenthesis it never closes")]
    [InlineData("AND name = 'open", "string opened at character 12 is never closed")]
    [InlineData("AND [open = 1", "quoted name opened at character 5 is never closed")]
    public void A_filter_that_is_not_one_predicate_is_refused_with_why(string filter, string why)
        => Assert.Contains(why, SourceFilter.Problem(filter), StringComparison.Ordinal);

    [Fact]
    public void A_filter_longer_than_a_read_takes_is_refused()
        => Assert.Contains("longer than 4000 characters", SourceFilter.Problem("AND " + new string('x', SourceFilter.MaxLength)), StringComparison.Ordinal);
}
