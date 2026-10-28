using System.Globalization;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What makes two blocked records one problem (osdu/docs/reference/concepts/record-lifecycle.md, Issues): the errors
/// the engine writes, with what names the record (a value, an id, a moment, a number, a file, a correlation id)
/// replaced, and nothing that names the problem.
/// </summary>
public class ProblemSignatureTests
{
    [Fact]
    public void A_render_hold_that_names_no_record_is_one_problem_and_keeps_the_digits_of_its_words()
    {
        const string hold = "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required";

        Assert.Equal(hold, ProblemSignature.Pattern(hold));
        Assert.Equal(ProblemSignature.Of(hold), ProblemSignature.Of(hold));
        Assert.NotEqual(ProblemSignature.Of(hold), ProblemSignature.Of(hold.Replace("Tag4", "Tag5", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_values_a_hold_quotes_are_the_record_s_and_not_the_problem_s()
    {
        var first = ProblemSignature.Pattern("osdu.data.WellboreID: 'WB-0001' matches no cached Wellbore, and the entry is required");
        var second = ProblemSignature.Pattern("osdu.data.WellboreID: 'Wellbore 17 (north)' matches no cached Wellbore, and the entry is required");

        Assert.Equal("osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required", first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, ProblemSignature.Pattern("osdu.data.WellID: 'WB-0001' matches no cached Wellbore, and the entry is required"));
    }

    [Fact]
    public void An_apostrophe_inside_a_word_opens_no_value()
    {
        const string error = "the record's row was deleted, and the flow's mapping reads it";

        Assert.Equal(error, ProblemSignature.Pattern(error));
        Assert.Equal("the value '<value>' is the record's", ProblemSignature.Pattern("the value 'x' is the record's"));
    }

    [Fact]
    public void A_refusal_keeps_the_service_and_what_it_said_and_loses_the_request_s_ids()
    {
        static string Refused(string correlation, string id)
            => $"HTTP 400 BadRequest from PUT https://osdu.example.com/api/storage/v2/records/dev:master-data--Wellbore:{id} (correlation-id {correlation}): "
                + "{\"code\":400,\"reason\":\"Invalid legal tags\",\"message\":\"Record dev:master-data--Wellbore:" + id + " names a legal tag that does not exist\"}";

        var first = ProblemSignature.Pattern(Refused("0f8fad5b-d9cb-469f-a165-70867728950e", "WB-1"));
        var second = ProblemSignature.Pattern(Refused("service-issued-7781", "WB-2"));

        Assert.Equal(
            "HTTP <n> BadRequest from PUT https://osdu.example.com/api/storage/v2/records/<id> (correlation-id <id>): "
            + "{\"code\":<n>,\"reason\":\"Invalid legal tags\",\"message\":\"Record <osdu id> names a legal tag that does not exist\"}",
            first);
        Assert.Equal(first, second);

        // Another status is another problem, by the reason the status line names.
        Assert.NotEqual(first, ProblemSignature.Pattern(Refused("x", "WB-1").Replace("BadRequest", "Forbidden", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_kind_is_kept_whole_while_a_record_id_is_replaced()
    {
        var pattern = ProblemSignature.Pattern(
            "refers to dev:master-data--Wellbore:WB-1:1695000000000, a osdu:wks:master-data--Wellbore:1.0.0 record no flow delivers, and dev:reference-data--UnitOfMeasure:m: as its unit");

        Assert.Equal("refers to <osdu id>, a osdu:wks:master-data--Wellbore:1.0.0 record no flow delivers, and <osdu id> as its unit", pattern);
    }

    [Fact]
    public void Moments_numbers_hashes_and_files_are_the_record_s()
    {
        Assert.Equal(
            ProblemSignature.Pattern("held since 2026-10-02 23:10:00Z: no payload files under /lake/wells/WB-1/logs; row 3 of 20260908T212727Z at 23:10:00.5"),
            ProblemSignature.Pattern("held since 2026-09-30T01:02:03.4567Z: no payload files under /lake/wells/WB-22/logs; row 412 of 20261001T000000Z-7 at 01:02:03"));
        Assert.Equal(
            "payload hash <hash> under <path> and <path> at data.Curves[<n>]",
            ProblemSignature.Pattern(@"payload hash 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 under C:\drop\WB-1\logs and abfss://lake@account.dfs.core.windows.net/wells/WB-1 at data.Curves[12]"));
    }

    [Fact]
    public void Escaped_values_in_a_quoted_answer_are_replaced_and_the_answer_s_own_words_kept()
    {
        Assert.Equal(
            "{\"message\":\"value \\\"<value>\\\" is not a valid date\"}",
            ProblemSignature.Pattern("{\"message\":\"value \\\"31/02/2020\\\" is not a valid date\"}"));
    }

    [Fact]
    public void Whitespace_collapses_and_an_error_with_nothing_said_is_the_problem_of_saying_nothing()
    {
        Assert.Equal("a b c", ProblemSignature.Pattern("  a \r\n b\t\tc "));
        Assert.Equal(ProblemSignature.NoReason, ProblemSignature.Pattern(null));
        Assert.Equal(ProblemSignature.NoReason, ProblemSignature.Pattern("   "));
        Assert.Equal(ProblemSignature.Of(null), ProblemSignature.Of(string.Empty));
    }

    [Fact]
    public void A_long_error_is_read_no_further_than_the_column_that_keeps_it()
    {
        var pattern = ProblemSignature.Pattern(new string('x', 5000));

        Assert.Equal(ProblemSignature.MaxPatternLength, pattern.Length);
        Assert.Equal(ProblemSignature.Of(new string('x', 2000)), ProblemSignature.Of(new string('x', 9000)));
    }

    [Fact]
    public void The_hash_is_the_first_eight_bytes_of_the_pattern_s_sha256_written_as_sixteen_hex_characters()
    {
        // Pinned: a ledger keeps these hashes, so a change to how they are made changes every problem it holds, and ships
        // with a migration that clears them for the backfill to sort again.
        Assert.Equal("41e2687b3a09debf", ProblemSignature.Format(ProblemSignature.Hash(ProblemSignature.NoReason)));
    }

    [Fact]
    public void The_values_an_error_names_are_what_its_row_held_in_order_and_without_their_quotes()
    {
        Assert.Equal(
            ["WB-1", "gAPI ", "31/02/2020"],
            ProblemSignature.Values("a: 'WB-1' and `gAPI ` and \\\"31/02/2020\\\" and '' at 2026-10-02T10:00:00Z, row 3, the record's"));
        Assert.Empty(ProblemSignature.Values("osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required"));
        Assert.Empty(ProblemSignature.Values(null));
        Assert.True(ProblemSignature.NamesValues(ProblemSignature.Pattern("value 'x' is wrong")));
        Assert.False(ProblemSignature.NamesValues(ProblemSignature.Pattern("value is wrong in row 4")));
    }

    [Fact]
    public void A_problem_whose_records_name_the_same_values_or_none_is_a_set_error_and_one_whose_records_differ_is_row_errors()
    {
        // The same mistake in every record of a prepared set: no value of the record's own, or the same value everywhere.
        Assert.Equal(ProblemShape.Set, ProblemSignature.ShapeOf(["osdu.tags.Tag4: dataset.tag4 is empty", "osdu.tags.Tag4: dataset.tag4 is empty"]));
        Assert.Equal(ProblemShape.Set, ProblemSignature.ShapeOf(["unit 'gAPI ' at row 3", "unit 'gAPI ' at row 98"]));
        Assert.Equal(ProblemShape.Set, ProblemSignature.ShapeOf(["refused (correlation-id a)", "refused (correlation-id b)"]));

        // Each record its own row's value.
        Assert.Equal(ProblemShape.Rows, ProblemSignature.ShapeOf(["'WB-1' matches no wellbore", "'WB-1' matches no wellbore", "'WB-2' matches no wellbore"]));
        Assert.Equal(ProblemShape.Set, ProblemSignature.ShapeOf([]));
    }

    [Fact]
    public void What_a_problem_s_records_share_is_written_in_and_where_they_differ_the_placeholder_stays()
    {
        static string Unit(int of, string cache) =>
            $"osdu.data.Curves[].CurveUnit, in {of} of 12 items: none of the 3 alternatives of $coalesce gives a value, and the entry is required: "
            + $"1. dataset.curves.curve_unit is empty; 2. no UnitOfMeasure matches 'gAPI ' in version {cache} of the cache; 3. dataset.curves.curve_unit is empty";
        var pattern = ProblemSignature.Pattern(Unit(3, "20261028T142555Z"));

        // The alternatives, their numbers, the value and the cache version are the same in both; how many items differs.
        Assert.Equal(
            "osdu.data.Curves[].CurveUnit, in <n> of 12 items: none of the 3 alternatives of $coalesce gives a value, and the entry is required: "
            + "1. dataset.curves.curve_unit is empty; 2. no UnitOfMeasure matches 'gAPI ' in version 20261028T142555Z of the cache; 3. dataset.curves.curve_unit is empty",
            ProblemSignature.Shared(pattern, [Unit(3, "20261028T142555Z"), Unit(1, "20261028T142555Z")]));

        // One error is said as it is; a third record naming another cache version turns that part back into its placeholder.
        Assert.Equal(Unit(3, "20261028T142555Z"), ProblemSignature.Shared(pattern, [Unit(3, "20261028T142555Z")]));
        Assert.Contains(
            "in version <time> of the cache",
            ProblemSignature.Shared(pattern, [Unit(3, "20261028T142555Z"), Unit(3, "20261028T142555Z"), Unit(3, "20261029T080000Z")]),
            StringComparison.Ordinal);

        // Row errors keep the value each row names; an address shared by every record is written whole.
        Assert.Equal(
            "osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required",
            ProblemSignature.Shared(
                "osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required",
                ["osdu.data.WellboreID: 'WB-1' matches no cached Wellbore, and the entry is required", "osdu.data.WellboreID: 'WB-2' matches no cached Wellbore, and the entry is required"]));
        const string Refused = "HTTP 403 Forbidden from PUT https://osdu.example.com/api/storage/v2/records (correlation-id {0})";
        Assert.Equal(
            "HTTP 403 Forbidden from PUT https://osdu.example.com/api/storage/v2/records (correlation-id <id>)",
            ProblemSignature.Shared(ProblemSignature.Pattern(string.Format(CultureInfo.InvariantCulture, Refused, "a")), [string.Format(CultureInfo.InvariantCulture, Refused, "a"), string.Format(CultureInfo.InvariantCulture, Refused, "b")]));
    }

    [Fact]
    public void An_error_of_another_problem_says_nothing_of_what_a_problem_s_records_share()
    {
        const string Pattern = "osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required";

        Assert.Equal(Pattern, ProblemSignature.Shared(Pattern, []));
        Assert.Equal(Pattern, ProblemSignature.Shared(Pattern, [null, " ", "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required"]));
        Assert.Equal(
            "osdu.data.WellboreID: 'WB-1' matches no cached Wellbore, and the entry is required",
            ProblemSignature.Shared(Pattern, ["something else entirely", "osdu.data.WellboreID: 'WB-1'  matches no cached Wellbore, and the entry is required"]));
        Assert.Equal(ProblemSignature.NoReason, ProblemSignature.Shared(ProblemSignature.NoReason, [null, ""]));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    public void A_problem_reads_back_as_it_was_written(long hash)
    {
        var text = ProblemSignature.Format(hash);

        Assert.Equal(ProblemSignature.TextLength, text.Length);
        Assert.True(ProblemSignature.TryParse(text, out var read));
        Assert.Equal(hash, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("41e2687b3a09deb")]
    [InlineData("41e2687b3a09debf0")]
    [InlineData("41e2687b3a09debg")]
    [InlineData("-1e2687b3a09debf")]
    [InlineData("0x2687b3a09debf0")]
    public void Anything_else_is_no_problem(string? text)
        => Assert.False(ProblemSignature.TryParse(text, out _));
}
