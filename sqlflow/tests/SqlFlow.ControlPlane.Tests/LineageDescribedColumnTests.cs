using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The column level of the described lineage, without a database: the one wildcard a mapped column may carry, and the
/// following of a column up through several described steps to the object a caller asks about. Three objects are
/// mapped here, a landed file from a curated table from a source table, with every shape a mapping takes: a renamed
/// column, a column made from two, a column the step makes itself, and a <c>*</c> entry for the rest.
/// </summary>
public sealed class LineageDescribedColumnTests
{
    private const string File = "file|landing/orders";
    private const string Curated = "described:lake|||curated.orders";
    private const string Source = "described:erp|||sales.orders";
    private const string Rates = "described:erp|||sales.rates";

    private static readonly Guid Publish = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Curate = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("order_id", "order_id", "")]
    [InlineData("order_id", "ORDER_ID", "")]
    [InlineData("*", "anything", "anything")]
    [InlineData("*", "", "")]
    [InlineData("raw:*", "raw:depth", "depth")]
    [InlineData("RAW:*", "raw:Depth", "Depth")]
    [InlineData("*_raw", "depth_raw", "depth")]
    [InlineData("a*z", "az", "")]
    public void Capture_SaysWhatTheStarStandsFor(string pattern, string name, string expected)
        => Assert.Equal(expected, ColumnGlob.Capture(pattern, name));

    [Theory]
    [InlineData("order_id", "order_no")]
    [InlineData("raw:*", "depth")]
    [InlineData("*_raw", "depth")]
    [InlineData("a*z", "a")]
    [InlineData("ab*ba", "aba")]
    public void Capture_IsNull_ForANameThatDoesNotFit(string pattern, string name)
        => Assert.Null(ColumnGlob.Capture(pattern, name));

    [Theory]
    [InlineData("orders.*", "depth", "orders.depth")]
    [InlineData("*", "depth", "depth")]
    [InlineData("fixed", "depth", "fixed")]
    public void Substitute_NamesTheSourceColumn(string source, string captured, string expected)
        => Assert.Equal(expected, ColumnGlob.Substitute(source, captured));

    [Fact]
    public async Task ANamedColumn_IsFollowedThroughTheStepInBetween_WithEachHopNamed()
    {
        var (composer, groups) = Estate(Source);

        var sources = await SourcesAsync(composer, groups, "amount");

        var source = Assert.Single(sources);
        Assert.Equal(Source, source.ObjectKey);
        Assert.Equal("amount", source.Column);
        var hop = Assert.Single(source.Via);
        Assert.Equal(Curated, hop.ObjectKey);
        Assert.Equal("amount_eur", hop.Column);
        Assert.Equal("curate", hop.Flow);
        Assert.Equal("amount * rate", hop.Note);
        Assert.False(composer.Truncated);
    }

    [Fact]
    public async Task AColumnMadeFromTwoObjects_AnswersOnlyForTheOneAskedAbout()
    {
        var (composer, groups) = Estate(Rates);

        var source = Assert.Single(await SourcesAsync(composer, groups, "amount"));

        Assert.Equal(Rates, source.ObjectKey);
        Assert.Equal("rate", source.Column);
    }

    [Fact]
    public async Task AStar_OneStepUp_IsTheStarOfThatObject()
    {
        var (composer, groups) = Estate(Curated);

        var source = Assert.Single(await SourcesAsync(composer, groups, "*"));

        Assert.Equal(Curated, source.ObjectKey);
        Assert.Equal("*", source.Column);
        Assert.Empty(source.Via);
    }

    [Fact]
    public async Task AStar_FurtherUp_IsEveryColumnItStandsFor_AndNotTheOnesItsDescriptionLists()
    {
        var (composer, groups) = Estate(Source);

        var sources = await SourcesAsync(composer, groups, "*");

        // The landed file lists amount by name, taken from amount_eur under another name. Its star stands for what it
        // does not list, so order_id and customer_id pass through under their own names, and amount_eur with them: the
        // file lists amount, not amount_eur.
        Assert.Equal(
            ["amount_eur>amount", "customer_id>customer_no", "order_id>order_no"],
            sources.Select(s => $"{Assert.Single(s.Via).Column}>{s.Column}").Order(StringComparer.Ordinal));
        Assert.All(sources, s => Assert.Equal(Source, s.ObjectKey));
    }

    [Fact]
    public async Task AStar_DoesNotStandForAColumnItsOwnDescriptionListsByName()
    {
        // The file lists order_id itself (made by the step, so with no source). The star must not answer for it.
        var rows = Rows();
        rows.Add(Row(Publish, "publish", File, "order_id", 9, 0, null, null));
        var (composer, groups) = Estate(Source, rows);

        var sources = await SourcesAsync(composer, groups, "*");

        Assert.DoesNotContain(sources, s => s.Via.Any(v => v.Column == "order_id"));
        Assert.Contains(sources, s => s.Via.Any(v => v.Column == "customer_id"));
    }

    [Fact]
    public async Task AColumnAskedForByName_IsAnsweredByTheStar_AndFollowedUnderThatName()
    {
        var (composer, groups) = Estate(Source);
        var star = groups.Single(g => g.Key.Column == "*");

        var source = Assert.Single(await composer.SourcesAsync(star, "customer_id", 8, DescribedLineage.ListedBy(groups, star.Key)));

        Assert.Equal("customer_no", source.Column);
        Assert.Equal("customer_id", Assert.Single(source.Via).Column);
    }

    [Fact]
    public async Task AColumnTheStepMakesItself_HasNoSource()
    {
        var (composer, groups) = Estate(Source);

        Assert.Empty(await SourcesAsync(composer, groups, "exported_at"));
        Assert.False(composer.Truncated);
    }

    [Fact]
    public async Task AnObjectThatIsNotUpstream_GivesNoSource_AndIsNotATruncation()
    {
        var (composer, groups) = Estate("described:erp|||sales.unrelated");

        Assert.Empty(await SourcesAsync(composer, groups, "amount"));
        Assert.False(composer.Truncated);
    }

    [Fact]
    public async Task TooFewSteps_CutsThePath_AndSaysSo()
    {
        var (composer, groups) = Estate(Source);
        var amount = groups.Single(g => g.Key.Column == "amount");

        Assert.Empty(await composer.SourcesAsync(amount, null, 1, DescribedLineage.ListedBy(groups, amount.Key)));
        Assert.True(composer.Truncated);
    }

    [Fact]
    public async Task MappingsThatFeedEachOther_End()
    {
        // Two objects, each mapping a column from the other: a mistake in the documents, never an endless walk.
        var a = "described:lake|||a";
        var b = "described:lake|||b";
        var rows = new List<CatalogLineageColumnMapping>
        {
            Row(Publish, "ab", a, "x", 1, 1, b, "x"),
            Row(Curate, "ba", b, "x", 1, 1, a, "x"),
        };
        var composer = Composer(Source, rows, maxObjects: 60);
        var start = DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == a)).Single();

        Assert.Empty(await composer.SourcesAsync(start, null, 12, DescribedLineage.ListedBy([start], start.Key)));
    }

    [Fact]
    public async Task PathsThatMultiplyAtEveryStep_AreCutByTheBudget_AndTheAnswerSaysSo()
    {
        // Six objects, four columns each from all four of the one before: 4^5 paths to the first object. Followed
        // without a budget, the work is the number of paths; with one, it ends and says it was cut.
        var (rows, first, last) = FanOut(objects: 6, width: 4);
        var composer = Composer(first, rows, maxObjects: 60, maxVisits: 200);
        var groups = DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == last));
        var column = groups.First();

        var sources = await composer.SourcesAsync(column, null, 12, DescribedLineage.ListedBy(groups, column.Key));

        Assert.True(composer.Truncated);
        Assert.True(sources.Count < 1024, $"{sources.Count} sources");
    }

    [Fact]
    public async Task TheSourcesOfOneColumn_AreCutAtTheirLimit_AndTheAnswerSaysSo()
    {
        var (rows, first, last) = FanOut(objects: 5, width: 4);
        var composer = Composer(first, rows, maxObjects: 60, maxSources: 10);
        var groups = DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == last));
        var column = groups.First();

        var sources = await composer.SourcesAsync(column, null, 12, DescribedLineage.ListedBy(groups, column.Key));

        Assert.Equal(10, sources.Count);
        Assert.True(composer.Truncated);
    }

    [Fact]
    public async Task AWalkThatFits_IsWhole_AndNotCalledCut()
    {
        var (rows, first, last) = FanOut(objects: 4, width: 3);
        var composer = Composer(first, rows, maxObjects: 60);
        var groups = DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == last));
        var column = groups.First();

        var sources = await composer.SourcesAsync(column, null, 12, DescribedLineage.ListedBy(groups, column.Key));

        Assert.Equal(27, sources.Count); // 3 x 3 x 3 paths, each a different way through
        Assert.False(composer.Truncated);
    }

    [Fact]
    public async Task ACancelledRequest_StopsTheWalk()
    {
        var (rows, first, last) = FanOut(objects: 6, width: 4);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var composer = Composer(first, rows, maxObjects: 60, ct: cancelled.Token);
        var groups = DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == last));
        var column = groups.First();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => composer.SourcesAsync(column, null, 12, DescribedLineage.ListedBy(groups, column.Key)));
    }

    [Fact]
    public async Task TheObjectAskedAbout_IsFoundWhateverCaseItsKeyIsWrittenIn()
    {
        var (composer, groups) = Estate(Source.ToUpperInvariant());

        var source = Assert.Single(await SourcesAsync(composer, groups, "amount"));

        Assert.Equal("amount", source.Column);
    }

    [Fact]
    public async Task TheObjectLimit_CutsTheWalk_AndSaysSo()
    {
        var rows = Rows();
        var composer = Composer(Source, rows, maxObjects: 0);
        var groups = DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == File));
        var amount = groups.Single(g => g.Key.Column == "amount");

        Assert.Empty(await composer.SourcesAsync(amount, null, 8, DescribedLineage.ListedBy(groups, amount.Key)));
        Assert.True(composer.Truncated);
    }

    private static Task<List<ColumnSourceDto>> SourcesAsync(
        DescribedLineage.ColumnComposer composer,
        List<IGrouping<DescribedLineage.ColumnGroupKey, CatalogLineageColumnMapping>> groups, string column)
    {
        var group = groups.Single(g => g.Key.Column == column);
        return composer.SourcesAsync(group, null, 8, DescribedLineage.ListedBy(groups, group.Key));
    }

    /// <summary>The composer over the mapped estate, asked about <paramref name="from"/>, with the mapped columns of
    /// the landed file.</summary>
    private static (DescribedLineage.ColumnComposer Composer,
        List<IGrouping<DescribedLineage.ColumnGroupKey, CatalogLineageColumnMapping>> Groups) Estate(
            string from, List<CatalogLineageColumnMapping>? rows = null)
    {
        rows ??= Rows();
        return (Composer(from, rows, maxObjects: 60), DescribedLineage.Group(rows.Where(r => r.TargetObjectKey == File)));
    }

    private static DescribedLineage.ColumnComposer Composer(
        string from, List<CatalogLineageColumnMapping> rows, int maxObjects, int maxVisits = 20000, int maxSources = 500,
        CancellationToken ct = default)
        => new(
            from, maxObjects, maxVisits, maxSources,
            key => Task.FromResult(DescribedLineage.Group(
                rows.Where(r => r.TargetObjectKey == key).OrderBy(r => r.Ordinal).ThenBy(r => r.SourceOrdinal))),
            ct);

    /// <summary>A chain of <paramref name="objects"/> objects in which every column of one is made from every column of
    /// the one before: the paths from the last to the first multiply by <paramref name="width"/> at every step.</summary>
    private static (List<CatalogLineageColumnMapping> Rows, string First, string Last) FanOut(int objects, int width)
    {
        var rows = new List<CatalogLineageColumnMapping>();
        static string Key(int i) => $"described:lake|||fan.{i}";
        for (var i = 1; i < objects; i++)
        {
            for (var column = 0; column < width; column++)
            {
                for (var source = 0; source < width; source++)
                {
                    rows.Add(Row(Curate, "fan", Key(i), $"c{column}", column + 1, source + 1, Key(i - 1), $"c{source}"));
                }
            }
        }

        return (rows, Key(0), Key(objects - 1));
    }

    private static List<CatalogLineageColumnMapping> Rows() =>
    [
        // The landed file, from the curated table.
        Row(Publish, "publish", File, "amount", 1, 1, Curated, "amount_eur", "Rounded to two decimals."),
        Row(Publish, "publish", File, "exported_at", 2, 0, null, null),
        Row(Publish, "publish", File, "*", 3, 1, Curated, "*"),

        // The curated table, from the source system.
        Row(Curate, "curate", Curated, "order_id", 1, 1, Source, "order_no"),
        Row(Curate, "curate", Curated, "customer_id", 2, 1, Source, "customer_no"),
        Row(Curate, "curate", Curated, "amount_eur", 3, 1, Source, "amount", "amount * rate"),
        Row(Curate, "curate", Curated, "amount_eur", 3, 2, Rates, "rate", "amount * rate"),
    ];

    private static CatalogLineageColumnMapping Row(
        Guid pipelineId, string flow, string target, string column, int ordinal, int sourceOrdinal,
        string? source, string? sourceColumn, string? note = null)
        => new()
        {
            PipelineId = pipelineId,
            Flow = flow,
            TargetObjectKey = target,
            TargetObjectName = target,
            TargetColumn = column,
            Ordinal = ordinal,
            SourceOrdinal = sourceOrdinal,
            SourceObjectKey = source,
            SourceObjectName = source,
            SourceColumn = sourceColumn,
            Note = note,
        };
}
