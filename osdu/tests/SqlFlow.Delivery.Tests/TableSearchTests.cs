using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a search of a look at a table is read, and the condition it narrows the rows to (<see cref="IngestionSql.TableSearch"/>):
/// the text found in every column a look shows as text, and a number or a moment also found whole where the database's text
/// of it cannot be the text shown. The SQL Server source runs it in <see cref="SqlServerIngestionSourceTests"/>.
/// </summary>
public sealed class TableSearchTests
{
    private static readonly SourceColumn Name = new("item_name", "nvarchar", 200, 0, 0, true);

    private static readonly SourceColumn Depth = new("depth", "float", 8, 53, 0, true);

    private static readonly SourceColumn Ratio = new("ratio", "real", 4, 24, 0, true);

    private static readonly SourceColumn Sampled = new("sampled", "datetime2", 8, 27, 7, true);

    private static readonly SourceColumn Stamped = new("stamped", "datetimeoffset", 10, 34, 7, true);

    private static readonly SourceColumn Blob = new("blob", "varbinary", 16, 0, 0, true);

    [Fact]
    public void A_search_reads_as_the_text_it_is_trimmed_and_as_the_number_or_moment_it_names()
    {
        var number = TableSearchTerm.Read("  1.5 ");
        Assert.Equal("%1.5%", number.Pattern);
        Assert.Equal(1.5, number.Float);
        Assert.Equal(1.5f, number.Real);
        Assert.Null(number.Moment);

        // A number a real cannot hold is a float alone; one no float holds is text alone.
        var large = TableSearchTerm.Read("1e300");
        Assert.Equal(1e300, large.Float);
        Assert.Null(large.Real);
        Assert.Null(TableSearchTerm.Read("1e999").Float);

        // A moment names its offset or is in UTC, down to the minute; a time of day or a date alone is text, as is a word.
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 10, 0, 0, TimeSpan.Zero), TableSearchTerm.Read("2024-05-06T12:00:00+02:00").Moment);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 10, 15, 0, 120, TimeSpan.Zero), TableSearchTerm.Read("2024-01-02T10:15:00.12Z").Moment);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 10, 15, 0, TimeSpan.Zero), TableSearchTerm.Read("2024-01-02T10:15").Moment);
        Assert.Null(TableSearchTerm.Read("10:15").Moment);
        Assert.Null(TableSearchTerm.Read("2024-01-02").Moment);
        var word = TableSearchTerm.Read("Wellbore");
        Assert.Equal((null, null, null), (word.Float, word.Real, word.Moment));

        // The pattern holds the text in upper case, its wildcards taken as written.
        Assert.Equal("%WELL%", TableSearchTerm.Read("well").Pattern);
        Assert.Equal($"%50{Escape}%{Escape}_%", TableSearchTerm.Read("50%_").Pattern);
        Assert.Throws<ArgumentException>(() => TableSearchTerm.Read("  "));
    }

    [Fact]
    public void A_search_finds_a_row_by_any_column_shown_as_text_and_by_a_whole_number_or_moment()
    {
        // A word is found in the text of every column but a binary one.
        var text = IngestionSql.TableSearch([Name, Depth, Ratio, Sampled, Stamped, Blob], TableSearchTerm.Read("well"));
        Assert.StartsWith("(", text, StringComparison.Ordinal);
        Assert.Contains($"UPPER(CAST(t.[item_name] AS nvarchar(4000))) LIKE {IngestionSql.SearchParameter}", text, StringComparison.Ordinal);
        Assert.Contains("UPPER(CAST(TRY_CAST(t.[depth] AS decimal(38, 15)) AS nvarchar(60)))", text, StringComparison.Ordinal);
        Assert.Contains("UPPER(CONVERT(nvarchar(40), t.[sampled], 126) + N'Z')", text, StringComparison.Ordinal);
        Assert.Contains("UPPER(CONVERT(nvarchar(40), SWITCHOFFSET(t.[stamped], '+00:00'), 127))", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[blob]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(IngestionSql.SearchFloatParameter, text, StringComparison.Ordinal);
        Assert.DoesNotContain(IngestionSql.SearchMomentParameter, text, StringComparison.Ordinal);

        // A number is found whole in each approximate column, as its own width reads it.
        var number = IngestionSql.TableSearch([Name, Depth, Ratio], TableSearchTerm.Read("1.5"));
        Assert.Contains($"t.[depth] = {IngestionSql.SearchFloatParameter}", number, StringComparison.Ordinal);
        Assert.Contains($"t.[ratio] = {IngestionSql.SearchRealParameter}", number, StringComparison.Ordinal);

        // A moment is found whole in the columns whose text keeps its fraction's trailing zeros.
        var moment = IngestionSql.TableSearch([Sampled, Stamped], TableSearchTerm.Read("2024-01-02T10:15:00.12Z"));
        Assert.Contains($"t.[sampled] = CAST({IngestionSql.SearchMomentParameter} AS datetime2(7))", moment, StringComparison.Ordinal);
        Assert.Contains($"t.[stamped] = {IngestionSql.SearchMomentParameter}", moment, StringComparison.Ordinal);

        // A table holding nothing a look shows as text finds no row.
        Assert.Equal("1 = 0", IngestionSql.TableSearch([Blob], TableSearchTerm.Read("QUJD")));
    }

    /// <summary>The escape character of <see cref="IngestionSql.ContainsPattern"/>.</summary>
    private static char Escape => (char)92;
}
