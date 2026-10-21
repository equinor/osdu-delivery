using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The search terms a pipeline gives (osdu/docs/reference/concepts/search-terms.md), read from the sample estate's
/// well log flow: the tables it reads and the mapping it renders them with. Every column of those tables the
/// mapping reads is a term (the table's column, whichever pipelines read it), each route by which the column reaches
/// the record, a value typed for a term put through the mapping as a render puts it, and a condition on a term turned
/// into the condition the explorer asks.
/// </summary>
public sealed class SearchTermTests
{
    private const string WellLog = "work-product-component--WellLog";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    /// <summary>The tables the sample flow reads: a log's row, and its curves' rows under the dataset <c>curves</c>.</summary>
    private const string Header = "OsduData.silver.WellLog";
    private const string Curves = "OsduData.silver.WellLogCurve";

    private static readonly MappingDefinition Mapping = new DeliveryDocumentLoader().LoadMapping(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));

    private static readonly SchemaSnapshot Template = Samples.SampleTemplate(WellLogVersions.CurrentKind);

    private static readonly SchemaSnapshot Wellbore = Samples.SampleTemplate(WellboreKind);

    /// <summary>The partition's units, captured from OSDU; the unit spellings the well database writes are a table of the database.</summary>
    private static SearchCacheType? CacheType(string name) => name switch
    {
        "UnitOfMeasure" => new SearchCacheType(
            "UnitOfMeasure", SearchCacheType.OsduOrigin, "osdu:wks:reference-data--UnitOfMeasure:*",
            [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name"), new ReferenceFieldSpec("data.ID")]),
        "UnitAlias" => new SearchCacheType("UnitAlias", "table", null, []),
        _ => null,
    };

    private static readonly SearchTermSource Flow = new(Mapping, Header, new Dictionary<string, string>(StringComparer.Ordinal) { ["curves"] = Curves });

    private static readonly IReadOnlyList<CompiledSearchTerm> Terms = SearchTermCompiler.Compile([Flow], CacheType, []);

    /// <summary>The sample mapping as a next version of it renders the same tables, under a source system of its own.</summary>
    private static readonly MappingDefinition Next = Mapping with
    {
        Version = "1.5.0",
        Template = Mapping.Template with { Kind = WellLogVersions.NextKind },
        Dataset = Mapping.Dataset with { System = "welldb-welllog-1.5.0" },
    };

    private static CompiledSearchTerm Term(string column, string? dataset = null)
        => Assert.Single(Terms, t => t.Key == SearchTermKey.Of(dataset is null ? Header : Curves, column) && t.EntityType == WellLog);

    private static SearchRoute Route(string column, string target, SearchRouteKind kind, string? dataset = null)
        => Assert.Single(Term(column, dataset).Routes, r => r.Target == target && r.Kind == kind);

    [Fact]
    public void A_column_written_as_it_stands_is_a_copy_and_one_through_modifiers_is_steps_keeping_what_they_keep()
    {
        var run = Route("log_run", "osdu.data.LogRun", SearchRouteKind.Copy);
        Assert.Equal(("data.LogRun", (string?)null), (run.Path, run.Problem));
        Assert.Equal(["WellLog@1.4.0"], run.Mappings);
        Assert.Equal([WellLogVersions.CurrentKind], run.Kinds);
        Assert.Equal(["welldb"], run.Systems);
        Assert.Equal((WellLog, (string?)null), (run.EntityType, run.Dataset));

        var name = Route("log_source", "osdu.data.Name", SearchRouteKind.Steps);
        Assert.Equal(["trim"], name.Steps);
        Assert.Equal(SearchRouteKeeps.Text, name.Keeps);

        var domain = Route("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps);
        Assert.Equal(["replace(DEPTH: Depth)", "ref"], domain.Steps);
        Assert.Equal((SearchRouteKeeps.Value, (string?)null), (domain.Keeps, domain.Problem));

        Assert.Equal(SearchRouteKeeps.Value, Route("depth_coding", "osdu.data.IsRegular", SearchRouteKind.Steps).Keeps);
        Assert.Equal(SearchRouteKeeps.Order, Route("update_date", "osdu.data.Curves[].DateStamp", SearchRouteKind.Steps, "curves").Keeps);
        Assert.Equal(SearchRouteKeeps.Text, Route("log_pass_type", "osdu.tags.LogStatus", SearchRouteKind.Steps).Keeps);
    }

    [Fact]
    public void A_column_a_search_finds_a_record_by_is_a_search_reading_the_records_id()
    {
        var wellbore = Assert.Single(Term("wellbore_uwi").Routes);

        Assert.Equal((SearchRouteKind.Search, "osdu.data.WellboreID", (string?)null), (wellbore.Kind, wellbore.Target, wellbore.Problem));
        Assert.Equal(
            new SearchRouteFind("Wellbore", "osdu:wks:master-data--Wellbore:*", [new SearchRouteLine("data.FacilityName"), new SearchRouteLine("data.NameAliases.AliasName")], "id"),
            wellbore.Find! with { Lines = wellbore.Find!.Lines.ToList() },
            new FindComparer());
    }

    [Fact]
    public void A_column_of_a_child_dataset_is_a_term_of_its_own_with_each_route_its_nodes_give()
    {
        var unit = Term("curve_unit", "curves");

        // The first and the last alternatives translate the spelling through a table of the database: one route, never
        // carried. The second finds the partition's unit by its ID, code or name: carried, by a search of the units. The
        // type captures a field called ID, so ID is that field, as the render reads it, and not the record id.
        Assert.Equal("WellLogCurve.curve_unit", unit.Key.ColumnLabel);
        Assert.All(unit.Routes, r => Assert.Equal("curves", r.Dataset));
        var table = Assert.Single(unit.Routes, r => r.Kind == SearchRouteKind.Steps);
        Assert.Contains("cached table UnitAlias", table.Problem, StringComparison.Ordinal);
        var lookup = Assert.Single(unit.Routes, r => r.Kind == SearchRouteKind.Lookup);
        Assert.Equal((2, (string?)null, "osdu:wks:reference-data--UnitOfMeasure:*"), (lookup.Alternative, lookup.Problem, lookup.Find!.Kind));
        Assert.Equal(["data.ID", "data.Code", "data.Name"], lookup.Find.Lines.Select(l => l.Field));
        Assert.Equal("osdu.data.Curves[].CurveUnit", lookup.Target);

        // A mnemonic is written twice as it is, and built into three ids from the curve dictionary, which no search reads.
        var curve = Term("curve_id", "curves");
        Assert.Equal(2, curve.Routes.Count(r => r.Kind == SearchRouteKind.Copy));
        Assert.All(curve.Routes.Where(r => r.Kind == SearchRouteKind.Steps), r => Assert.Contains("cached table CurveDictionary", r.Problem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_column_of_the_key_makes_the_records_id_with_the_others_the_record_holds()
    {
        var id = Assert.Single(Term("log_id").Routes);

        Assert.Equal((SearchRouteKind.Key, "id", (string?)null), (id.Kind, id.Path, id.Problem));
        Assert.Equal(["welldb"], id.Key!.Systems);
        Assert.False(id.Key.FromKey);
        Assert.Equal(["source_project", "log_id"], id.Key.Columns);
        Assert.Equal("tags.SourceProject", id.Key.Others!["source_project"]);

        // The project is written as it stands too, which is the route it is searched by first.
        Assert.Equal(2, Term("source_project").Routes.Count);
    }

    [Fact]
    public void Who_may_read_a_record_and_its_legal_block_are_not_terms()
    {
        Assert.DoesNotContain(Terms.SelectMany(t => t.Routes), r => r.Path.StartsWith("acl.", StringComparison.Ordinal) || r.Path.StartsWith("legal.", StringComparison.Ordinal));
        Assert.All(Terms.SelectMany(t => t.Routes), r => Assert.Equal(["welldb"], r.Systems));
        Assert.All(Terms, t => Assert.Contains(t.Key.Source, new[] { Header, Curves }));
    }

    [Fact]
    public void A_lookup_through_a_type_no_cache_flow_declares_or_one_not_of_osdu_says_why_it_cannot_be_searched()
    {
        var terms = SearchTermCompiler.Compile([Flow], _ => null, []);
        var lookup = Assert.Single(Assert.Single(terms, t => t.Key.Column == "curve_unit").Routes, r => r.Kind == SearchRouteKind.Lookup);

        Assert.Contains("declared by no cache flow", lookup.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_pipelines_rendering_one_table_under_two_versions_give_one_term_whose_routes_both_read()
    {
        // The estate's WellLog 1.4.0 and 1.5.0 flows read the same tables, each mapping under a source system of its own.
        var next = new SearchTermSource(Next, "[OsduData].[silver].[WellLog]", new Dictionary<string, string>(StringComparer.Ordinal) { ["curves"] = Curves });
        var terms = SearchTermCompiler.Compile([next, Flow], CacheType, []);

        var uwi = Assert.Single(terms, t => t.Key.Column == "wellbore_uwi");
        var run = Assert.Single(Assert.Single(terms, t => t.Key.Column == "log_run").Routes);
        Assert.Equal(["WellLog@1.4.0", "WellLog@1.5.0"], run.Mappings);
        Assert.Equal([WellLogVersions.CurrentKind, WellLogVersions.NextKind], run.Kinds);
        Assert.Equal(["welldb", "welldb-welllog-1.5.0"], run.Systems);
        Assert.Equal("WellLog.wellbore_uwi", uwi.Key.ColumnLabel);
        Assert.Equal(Header, uwi.Key.Source);
        Assert.Equal(Terms.Count, terms.Count);

        // The record's id is made as each version's delivery makes it.
        var id = Assert.Single(Assert.Single(terms, t => t.Key.Column == "log_id").Routes);
        Assert.Equal(["welldb", "welldb-welllog-1.5.0"], id.Key!.Systems);
    }

    [Fact]
    public void A_column_of_another_table_is_another_term_and_a_dataset_the_flow_does_not_declare_is_reported()
    {
        var other = new SearchTermSource(Mapping, "Staging.dbo.WellLogExport", new Dictionary<string, string>(StringComparer.Ordinal));
        var warnings = new List<string>();
        var terms = SearchTermCompiler.Compile([Flow, other], CacheType, warnings);

        Assert.Equal(2, terms.Count(t => t.Key.Column == "wellbore_uwi"));
        Assert.Contains(terms, t => t.Key == SearchTermKey.Of("Staging.dbo.WellLogExport", "wellbore_uwi") && t.Key.ColumnLabel == "WellLogExport.wellbore_uwi");
        Assert.DoesNotContain(terms, t => t.Key.Column == "curve_unit" && t.Key.Source == "Staging.dbo.WellLogExport");
        Assert.Contains(warnings, w => w.Contains("dataset curves", StringComparison.Ordinal) && w.Contains("Staging.dbo.WellLogExport", StringComparison.Ordinal));
    }

    [Fact]
    public void A_terms_identity_is_its_table_and_column_whatever_their_case_or_brackets()
    {
        var key = SearchTermKey.Of(" [OsduData].[silver].[WellLog] ", "Wellbore_UWI");

        Assert.Equal("osdudata.silver.welllog/wellbore_uwi", key.Text);
        Assert.Equal(SearchTermKey.Of("OsduData.silver.WellLog", "wellbore_uwi").Id, key.Id);
        Assert.NotEqual(SearchTermKey.Of(Curves, "wellbore_uwi").Id, key.Id);
        Assert.Equal("WellLog", key.Table);
        Assert.Equal("WellLog.Wellbore_UWI", key.ColumnLabel);
        Assert.Equal(("welldb", WellLog, "curves", "curve_unit"), SearchTermKey.Legacy("welldb/work-product-component--WellLog/curves/curve_unit"));
        Assert.Null(SearchTermKey.Legacy(key.Text));
    }

    [Theory]
    [InlineData("log_source", null, "osdu.data.Name", SearchRouteKind.Steps, "  COMPOSITE ", "COMPOSITE")]
    [InlineData("index_type", null, "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps, "DEPTH", "dev:reference-data--WellLogSamplingDomainType:Depth:")]
    [InlineData("depth_coding", null, "osdu.data.IsRegular", SearchRouteKind.Steps, "REGULAR", "true")]
    [InlineData("depth_coding", null, "osdu.data.IsRegular", SearchRouteKind.Steps, "IRREGULAR", "false")]
    [InlineData("log_pass", null, "osdu.data.LogActivity", SearchRouteKind.Steps, "MAIN,REPEAT", "MAIN")]
    [InlineData("index_min", null, "osdu.data.SamplingStart", SearchRouteKind.Copy, "1500.5", "1500.5")]
    [InlineData("business_value", "curves", "osdu.data.Curves[].LogCurveBusinessValueID", SearchRouteKind.Steps, "HIGH", "dev:reference-data--LogCurveBusinessValue:High:")]
    [InlineData("wellbore_uwi", null, "osdu.data.WellboreID", SearchRouteKind.Search, "WB D/4-A-30", "WB D/4-A-30")]
    [InlineData("log_id", null, "id", SearchRouteKind.Key, " LOG-0001/1 ", "LOG-0001/1")]
    public void A_value_typed_for_a_term_is_put_through_the_mapping_as_the_render_puts_it(
        string column, string? dataset, string target, SearchRouteKind kind, string typed, string written)
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");
        var key = SearchTermKey.Of(dataset is null ? Header : Curves, column);

        var carried = values.Translate(key, Route(column, target, kind, dataset), typed);

        Assert.Equal(SearchValue.Of(written), carried);
    }

    [Fact]
    public void A_value_the_render_would_hold_a_record_for_is_refused_with_why()
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");

        var notANumber = values.Translate(SearchTermKey.Of(Header, "index_min"), Route("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy), "deep");
        Assert.Null(notANumber.Value);
        Assert.Contains("deep", notANumber.Problem, StringComparison.Ordinal);

        var empty = values.Translate(SearchTermKey.Of(Header, "log_source"), Route("log_source", "osdu.data.Name", SearchRouteKind.Steps), "   ");
        Assert.Null(empty.Value);

        var table = values.Translate(SearchTermKey.Of(Curves, "curve_unit"), Route("curve_unit", "osdu.data.Curves[].CurveUnit", SearchRouteKind.Steps, "curves"), "GAPI");
        Assert.Contains("UnitAlias", table.Problem, StringComparison.Ordinal);

        Assert.Throws<FlowValidationException>(() => SearchTermValues.For(Mapping, Template, "not a partition"));
    }

    [Fact]
    public void The_conditions_a_route_takes_follow_its_index_and_what_its_steps_keep()
    {
        IReadOnlyList<ExplorerCondition> Conditions(string column, string target, SearchRouteKind kind, string? dataset = null, SchemaSnapshot? found = null)
        {
            var route = Route(column, target, kind, dataset);
            return SearchTermResolver.Conditions(route, SearchTermResolver.Classify(route, Template, found));
        }

        Assert.Equal(
            [ExplorerCondition.Contains, ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf, ExplorerCondition.StartsWith, ExplorerCondition.Exists, ExplorerCondition.Missing],
            Conditions("log_source", "osdu.data.Name", SearchRouteKind.Steps));
        Assert.Equal(
            [ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf, ExplorerCondition.Exists, ExplorerCondition.Missing],
            Conditions("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps));
        Assert.Equal(ExplorerCondition.Range, Conditions("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy)[0]);
        Assert.Equal([ExplorerCondition.Is, ExplorerCondition.Exists, ExplorerCondition.Missing], Conditions("depth_coding", "osdu.data.IsRegular", SearchRouteKind.Steps));

        // Through a search of wellbores matched on a name and a nested alias: words, but no start, which a nested list does not take.
        var wellbore = Conditions("wellbore_uwi", "osdu.data.WellboreID", SearchRouteKind.Search, found: Wellbore);
        Assert.Contains(ExplorerCondition.Contains, wellbore);
        Assert.DoesNotContain(ExplorerCondition.StartsWith, wellbore);

        // Inside the curves, a nested list, nothing asks whether a value is held.
        Assert.DoesNotContain(ExplorerCondition.Exists, Conditions("curve_description", "osdu.data.Curves[].CurveDescription", SearchRouteKind.Copy, "curves"));
        Assert.Equal([ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf], Conditions("log_id", "id", SearchRouteKind.Key));
    }

    [Fact]
    public void A_route_through_records_no_template_the_mapping_reads_describes_cannot_be_searched_and_says_so()
    {
        var route = Route("wellbore_uwi", "osdu.data.WellboreID", SearchRouteKind.Search);

        var fields = SearchTermResolver.Classify(route, Template, found: null);

        Assert.Null(fields.Target);
        Assert.Contains("no template the mapping reads of osdu:wks:master-data--Wellbore:*", fields.Problem, StringComparison.Ordinal);
        Assert.Contains("put one in a template folder on the mapping's path", fields.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_on_a_term_is_the_condition_on_the_property_its_route_fills_with_the_values_carried()
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");
        ExplorerFilter Resolve(string column, string target, SearchRouteKind kind, SearchTermCondition asked, SchemaSnapshot? found = null)
        {
            var route = Route(column, target, kind);
            return SearchTermResolver.Resolve(column, SearchTermKey.Of(Header, column), route, SearchTermResolver.Classify(route, Template, found), asked, values);
        }

        var domain = Resolve("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps, new SearchTermCondition(ExplorerCondition.AnyOf, Values: ["DEPTH", "TIME"]));
        Assert.Equal(("data.SamplingDomainTypeID", OsduFieldIndex.Text, (ExplorerVia?)null), (domain.Path, domain.Index, domain.Via));
        Assert.Equal(["dev:reference-data--WellLogSamplingDomainType:Depth:", "dev:reference-data--WellLogSamplingDomainType:TIME:"], domain.Values!);
        Assert.Equal("(data.SamplingDomainTypeID.keyword:(\"dev:reference-data--WellLogSamplingDomainType:Depth:\" OR \"dev:reference-data--WellLogSamplingDomainType:TIME:\"))", domain.Clause());

        var depth = Resolve("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy, new SearchTermCondition(ExplorerCondition.Range, "1000", To: "2000"));
        Assert.Equal("data.SamplingStart:[\"1000\" TO \"2000\"}", depth.Clause());

        var wellbore = Resolve("wellbore_uwi", "osdu.data.WellboreID", SearchRouteKind.Search, new SearchTermCondition(ExplorerCondition.Is, "WB D/4-A-30"), Wellbore);
        Assert.Equal(("data.WellboreID", "WB D/4-A-30"), (wellbore.Path, wellbore.Value));
        Assert.Equal(("wellbore_uwi", "osdu:wks:master-data--Wellbore:*", "id"), (wellbore.Via!.Term, wellbore.Via.Kind, wellbore.Via.Read!.Path));
        Assert.Equal(
            "(data.FacilityName.keyword:\"WB D/4-A-30\") OR (nested(data.NameAliases, (AliasName.keyword:\"WB D/4-A-30\")))",
            wellbore.Via.Matching(wellbore).Text);

        var log = Resolve("log_id", "id", SearchRouteKind.Key, new SearchTermCondition(ExplorerCondition.Is, "LOG-0001/1"));
        Assert.Equal(("id", OsduFieldIndex.Keyword, "log_id", WellLog), (log.Path, log.Index, log.Via!.Key!.Given, log.Via.Key.EntityType));
        Assert.Equal(["welldb"], log.Via.Key.Systems);
        Assert.Equal(new ExplorerViaColumn { Column = "source_project", Path = "tags.SourceProject", Index = OsduFieldIndex.Keyword }, Assert.Single(log.Via.Key.Others));

        var exists = Resolve("log_run", "osdu.data.LogRun", SearchRouteKind.Copy, new SearchTermCondition(ExplorerCondition.Missing));
        Assert.Equal("NOT (_exists_:data.LogRun)", exists.Clause());
    }

    [Fact]
    public void A_condition_a_route_does_not_take_or_a_value_it_cannot_carry_is_refused_with_why()
    {
        var values = SearchTermValues.For(Mapping, Template, "dev");
        var domain = Route("index_type", "osdu.data.SamplingDomainTypeID", SearchRouteKind.Steps);
        var fields = SearchTermResolver.Classify(domain, Template, null);
        var key = SearchTermKey.Of(Header, "index_type");

        var words = Assert.Throws<DeliveryException>(() => SearchTermResolver.Resolve("Domain", key, domain, fields, new SearchTermCondition(ExplorerCondition.Contains, "DEP"), values));
        Assert.Contains("not Contains", words.Message, StringComparison.Ordinal);

        var start = Route("index_min", "osdu.data.SamplingStart", SearchRouteKind.Copy);
        var deep = Assert.Throws<DeliveryException>(() => SearchTermResolver.Resolve(
            "Start", SearchTermKey.Of(Header, "index_min"), start, SearchTermResolver.Classify(start, Template, null), new SearchTermCondition(ExplorerCondition.Is, "deep"), values));
        Assert.Contains("Start 'deep' cannot be searched for", deep.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_route_a_term_is_searched_by_is_the_one_picked_while_it_can_be_else_the_plainest()
    {
        var routes = Term("source_project").Routes;

        Assert.Equal(SearchRouteKind.Copy, SearchTermResolver.Preferred(routes, _ => true, picked: null)!.Kind);
        Assert.Equal(SearchRouteKind.Key, SearchTermResolver.Preferred(routes, _ => true, picked: $"{WellLog}|id|Key")!.Kind);
        Assert.Equal(SearchRouteKind.Copy, SearchTermResolver.Preferred(routes, r => r.Kind != SearchRouteKind.Key, picked: $"{WellLog}|id|Key")!.Kind);
        Assert.Null(SearchTermResolver.Preferred(routes, _ => false, picked: null));
    }

    [Fact]
    public void Where_versions_write_a_column_differently_the_route_of_the_kind_in_view_is_searched()
    {
        // The next version writes the run into a property of its own; every other route is read by both versions.
        var current = Route("log_run", "osdu.data.LogRun", SearchRouteKind.Copy);
        var next = current with { Target = "osdu.data.LogRunNumber", Path = "data.LogRunNumber", Kinds = [WellLogVersions.NextKind], Mappings = [WellLogVersions.NextMapping] };
        IReadOnlyList<SearchRoute> routes = [current, next];

        Assert.Same(next, SearchTermResolver.Preferred(routes, _ => true, picked: null, kind: WellLogVersions.NextKind));
        Assert.Same(current, SearchTermResolver.Preferred(routes, _ => true, picked: null, kind: WellLogVersions.CurrentKind));
        Assert.Same(current, SearchTermResolver.Preferred(routes, _ => true, picked: null, kind: $"*:*:{WellLog}:*"));

        // A route picked holds for the kinds it fills; another kind in view searches its own.
        Assert.Same(next, SearchTermResolver.Preferred(routes, _ => true, picked: next.Id, kind: $"*:*:{WellLog}:*"));
        Assert.Same(current, SearchTermResolver.Preferred(routes, _ => true, picked: next.Id, kind: WellLogVersions.CurrentKind));
    }

    /// <summary>Two finds alike: the same records, lines and read, the lines compared by value.</summary>
    private sealed class FindComparer : IEqualityComparer<SearchRouteFind>
    {
        public bool Equals(SearchRouteFind? x, SearchRouteFind? y)
            => x is not null && y is not null && x.Name == y.Name && x.Kind == y.Kind && x.Read == y.Read && x.Lines.SequenceEqual(y.Lines);

        public int GetHashCode(SearchRouteFind obj) => HashCode.Combine(obj.Name, obj.Kind, obj.Read);
    }
}
